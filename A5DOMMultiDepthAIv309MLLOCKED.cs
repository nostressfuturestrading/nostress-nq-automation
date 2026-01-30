#region Using declarations
using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using System.Diagnostics;
using System.Security.Cryptography;
#endregion

// =======================================================================================
// A5 DOM RECORDER v309 (ML LOCKED + WAN SAFE)
// =======================================================================================
// PATCH 1: Price-Aware OFI (Cont et al.) - Fixes inverted signs on price moves
// PATCH 2: OFI Shock Reset - Resets stats on price regime change
// PATCH 3: WAN Safety - Tanh normalization for velocity/accel
// PATCH 4: Jitter Guard - Minimum dt floor (5ms) to prevent gradient explosions
// PATCH 5: Latency Visibility - Extended histogram to 2000ms
// PATCH 6: Causal Integrity - Explicit training block on inferred LeadLag
// PATCH 7: Session Reset - Clears alignment buffers on startup
// PATCH 8: Metadata - Flags "wan_normalized" for downstream parsers
// =======================================================================================

namespace NinjaTrader.NinjaScript.Indicators
{

    public class A5_DOM_MultiDepth_AI_v309_ML_LOCKED : Indicator
    {

    public static class Tags { public const byte NONE = 0; public const byte DOM = 1; public const byte TRADE = 2; public const byte SYS_ROTATE = 3; public const byte HEARTBEAT = 4; }
    public static class Reasons { public const byte NONE=0; public const byte TIMER=1; public const byte TRADE=2; public const byte QUOTE=3; public const byte VOL=4; public const byte RISK=5; public const byte BOOTSTRAP=6; public const byte FATAL_CROSS=7; public const byte FORCED_PRE_TRADE=8; public const byte INSERT_EVENT=9; public const byte REWRITE_FIELDS=10; public const byte GAP_FILL=11; public const byte TIME_REGRESSION=12; public const byte PRE_TRADE_SYNC=13; }
    public static class States { public const byte ACTIVE=1; public const byte IDLE=2; public const byte IMPACT=3; }
    public static class LiqEvents { public const byte NONE=0; public const byte ADD=1; public const byte CANCEL=2; public const byte MODIFY=3; }
    public static class Aggressors { public const byte NA=0; public const byte B=1; public const byte S=2; public const byte M=3; public const byte U=4; }
    public static class BookAges { public const byte STALE=0; public const byte FRESH=1; public const byte WARM=2; }
    public static class RepairTypes { public const byte NONE=0; public const byte STRUCTURAL=1; public const byte ANNOTATION=2; public const byte SOFT=3; }
    public static class RepairReasons { public const byte NONE=0; public const byte GAP_FILL=1; public const byte INSERT_EVENT=2; public const byte REWRITE_FIELDS=3; public const byte DOM_AGE_UPDATE=4; public const byte CROSSED_BOOK=5; }
    public static class RepairMethods { public const byte NONE=0; public const byte SNAP_FORWARD=1; }
    public static class RepairRules { public const byte NONE=0; public const byte TIME_SKEW_CHK=1; public const byte CB_FIX_HARD=2; }
    public static class CausalLabels { public const byte NONE=0; public const byte PRE_TRADE=1; public const byte POST_TRADE=2; public const byte NO_DOM_CONTEXT=3; public const byte TIMEOUT=4; }
    public static class PolReasons { public const byte NONE=0; public const byte STRUCTURAL_REPAIR=1; public const byte CAUSAL_FAIL=2; public const byte LATENCY_GT_3S=3; public const byte POLICY_FAIL=4; public const byte WIDE_SPREAD=5; public const byte EXCHANGE_BURST=6; }
    public static class AnchorTypes { public const byte NONE=0; public const byte STRICT_MATCH=1; public const byte BEST_PRIOR=2; }
    public static class AnchorReasons { public const byte NONE=0; public const byte EXCH_BATCH_AMBIGUITY=1; public const byte NO_PRIOR_HISTORY=2; }
    public static class BurstTypes { public const byte NONE=0; public const byte SAME_EXCH_TS=1; public const byte PRICE_CLUSTER=2; }

        // ==========================================================
        // VERSION / CONSTANTS
        // ==========================================================
        private const string VERSION = "A5_DOM_MultiDepth_AI_v309_ML_LOCKED";
        private const int HARD_MAX_DEPTH = 20;
        private const int HISTORY_BUFFER_SIZE = 1024; // Power of 2 for safety

        // ==========================================================
        // PARAMETERS (LOCKED)
        // ==========================================================
        private DateTime sessionStartUtc;
        private bool warmup = true;

        [NinjaScriptProperty]
        [Range(1, HARD_MAX_DEPTH)]
        [Display(Name = "Max Depth (1-20)", GroupName = "Recorder", Order = 0)]
        public int MaxDepth { get; set; } = 20;

        [NinjaScriptProperty]
        [Range(1, HARD_MAX_DEPTH)]
        [Display(Name = "Pressure Levels (K)", GroupName = "Features", Order = 1)]
        public int PressureLevels { get; set; } = 10;

        [NinjaScriptProperty]
        [Range(1, HARD_MAX_DEPTH)]
        [Display(Name = "DepthOfi Levels (K)", GroupName = "Features", Order = 2)]
        public int DepthOfiLevels { get; set; } = 10;

        [NinjaScriptProperty]
        [Range(1, HARD_MAX_DEPTH)]
        [Display(Name = "QueueImb Levels (K)", GroupName = "Features", Order = 3)]
        public int QueueImbLevels { get; set; } = 5;

        [NinjaScriptProperty]
        [Display(Name = "Write Fused File", GroupName = "Recorder", Order = 4)]
        public bool WriteFused { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Use Session Filter", GroupName = "Recorder", Order = 5)]
        public bool UseSessionFilter { get; set; } = false;

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "Flush (ms)", GroupName = "Performance", Order = 6)]
        public int FlushMs { get; set; } = 500;

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Min Snapshot Interval (ms) [DOM]", GroupName = "Performance", Order = 7)]
        public int MinSnapshotIntervalMs { get; set; } = 5;

        [NinjaScriptProperty]
        [Display(Name = "Secondary Instrument", GroupName = "Data", Order = 8)]
        public string SecondaryInstrument { get; set; } = "ES 03-26";

        [NinjaScriptProperty]
        [Range(0, 300)]
        [Display(Name = "Heartbeat (sec)", GroupName = "Performance", Order = 9)]
        public int HeartbeatSec { get; set; } = 60;

        // --- Spoof detection (heuristic, NT8-safe) ---
        [NinjaScriptProperty]
        [Display(Name = "Enable Spoof Flags", GroupName = "Spoof", Order = 10)]
        public bool EnableSpoof { get; set; } = true;

        [NinjaScriptProperty]
        [Range(10, 2000)]
        [Display(Name = "Spoof Window (ms)", GroupName = "Spoof", Order = 11)]
        public int SpoofWindowMs { get; set; } = 250;

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "Spoof Min Add Size", GroupName = "Spoof", Order = 12)]
        public int SpoofMinAddSize { get; set; } = 20;

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Spoof Max Ticks From Touch", GroupName = "Spoof", Order = 13)]
        public int SpoofMaxTicksFromTouch { get; set; } = 2;

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(Name = "Spoof Cancel Fraction", GroupName = "Spoof", Order = 14)]
        public double SpoofCancelFrac { get; set; } = 0.7;

        // --- Iceberg detection (heuristic, NT8-safe) ---
        [NinjaScriptProperty]
        [Display(Name = "Enable Iceberg Flags", GroupName = "Iceberg", Order = 20)]
        public bool EnableIceberg { get; set; } = true;

        [NinjaScriptProperty]
        [Range(10, 3000)]
        [Display(Name = "Iceberg Window (ms)", GroupName = "Iceberg", Order = 21)]
        public int IcebergWindowMs { get; set; } = 300;

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "Iceberg Min Trade Vol", GroupName = "Iceberg", Order = 22)]
        public int IcebergMinTradeVol { get; set; } = 5;

        [NinjaScriptProperty]
        [Range(0.1, 2.0)]
        [Display(Name = "Iceberg Min Visible Fill Fraction", GroupName = "Iceberg", Order = 23)]
        public double IcebergMinVisibleFillFrac { get; set; } = 0.5;

        // ==========================================================
        // INTERNAL STATE
        // ==========================================================
        // 1. LightDomPayload (Stack Memory / Value Type)
        public struct LightDomPayload
        {
            // Header
            public long Seq;
            public long GlobalSeq;
            public DateTime ExchTs;
            public DateTime HostUtc;
            public long InstSeq;
            public DateTime RawExchTs;
            public int ExchRegress;

            // [NEW] Exchange Timing
            public double DeltaExchMs;

            // [NEW] Trade Impact
            public int AggrTicks;

            // [NEW] Granular Liquidity Details
            public int LiqSide;   // 0=None, 1=Bid, -1=Ask
            public int LiqLevel;  // 0 to 9, or -1 if outside depth
            public long LiqDelta; // +Size or -Size

            // Trigger / Meta
            public bool IsTrade;
            public long TradeVol;
            public double LastPx;
            public byte Tag;
            public byte Reason;
            public byte State;
            public byte LiquidityEvent;
            public byte Aggr;

            public long Arg1;

            // Raw State
            public bool Crossed;
            public bool Locked;
            public double RawBid, RawAsk;

            // Book Age
            public double BookAgeMs;
            public byte BookAgeClass;
            public long DomSeqRef;

            // Price Match
            public bool PxExact;
            public int PxTicksDiff;
            public bool PxWithinSpread;
            public bool CausalPass;

            // Repair
            public byte RepairType;
            public byte RepairReason;
            public byte RepairMethod;
            public byte RepairRuleId;
            public double RepairConf;

            // Execution
            public bool ExecFeasible;
            public long ExecLatency;
            public int ExecBlockedMask;

            // Causality
            public byte CausalityLabel;
            public int CausalityWindow;
            public bool CausalityLocked;
            public double CausalityScore;

            // Policy
            public bool PolWarmup;
            public bool PolRth;
            public bool PolTrainRes;
            public bool PolTrainExec;
            public bool PolExecLive;
            public byte PolReason;

            // Causal Anchor
            public long AnchorDomSeq;
            public byte AnchorType;
            public double AnchorExchDelta;
            public double AnchorHostDelta;
            public double AnchorConf;
            public byte AnchorReason;
            public bool HasDomContext;

            // Train
            public bool TrainEligible;
            public byte TrainBlockReason;
            public double StableMs;

            // Burst
            public long BurstId;
            public int BurstCnt;
            public byte BurstType;

            // Clock
            public bool ClockMonotonic;
            public long DtMs;
            public long Latency;

            // Prices
            public double Bid, Ask;
            public double Spread;
            public int SpreadTicks;

            // ===== ML SIGNALS =====
            public double ImbL1;
            public double SweepTicks;
            public double SpreadBps;

            public int Sig;
            public double Llr;
            public double LeadLagMs;
            public double LeadLagCorr;
            public double NqZ;
            public double EsZ;

            // Realized Volatility
            public double Rv1s, Rv5s, Rv30s;
            // --- ML Feature: Queue Pos ---
            public double QueuePosBid;
            public double QueuePosAsk;
            // --- ML Feature: Spread Stability ---
            public long MsInSpread;
            // --- ML Feature: OFI Shock ---
            public int OfiShock;
            public double MidVelTicks;
            public double MidAccelTicks;
            public int TopFlipCount1s;
            public double SpreadVel;
            public double OfiSkewEma;
            public double CancelAddRatio1s;

            // --- ML Feature: Price Kinematics ---
            public double MidVel;
            public double MidAccel;

            // [PATCH 3] Raw Physics
            public double MidVelRaw;
            public double MidAccelRaw;
            public double SpreadVelRaw;

            // [PATCH 5] Market Regime
            public string MarketRegime;

            // [PATCH 7] OFI Z-Score
            public double OfiZ;

            // [PATCH 8] Structural Density
            public long StructuralRate1s;

            // --- ML Feature: Signed Trade Imbalance ---
            public long TradeImb1s;

            // [PATCH 9] Session Stats
            public long SessionVol;
            public double SessionVWAP;

            // --- ML Feature: Queue Refill Speed ---
            public double BidRefillRate;
            public double AskRefillRate;

            // --- ML Label Gate ---
            public bool LabelEligibleLive;

            // --- Feature 2: Time-Since ---
            public long MsSinceTrade;
            public long MsSinceTop;
            public long MsSinceSpread;

            // --- Feature 3: Slopes ---
            public double PressureSlope;
            public double QimbSlope;

            // --- Feature 4: Event Density ---
            public int DomEvents100ms;
            public int TradeEvents100ms;
            public int CancelEvents100ms;

            // Features
            public long TouchBid, TouchAsk;
            public long L1Bid;
            public long L1Ask;
            public long SumBid, SumAsk;
            public double ConcBid, ConcAsk;
            public bool VoidBid, VoidAsk;
            public int GapBid, GapAsk;
            public double Micro;
            public long Ofi;
            public long DepthOfi;
            public long ChurnK;
            public double CancelK;
            public double Pressure, Pressure1;
            public double QImb, QImb1;
            public int Spoof, Iceberg;
            public int XB;

            // Depth (Top 10 Flattened to avoid Array Alloc)
            public double BP0, BP1, BP2, BP3, BP4, BP5, BP6, BP7, BP8, BP9;
            public long BS0, BS1, BS2, BS3, BS4, BS5, BS6, BS7, BS8, BS9;
            public long BD0, BD1, BD2, BD3, BD4, BD5, BD6, BD7, BD8, BD9;

            public double AP0, AP1, AP2, AP3, AP4, AP5, AP6, AP7, AP8, AP9;
            public long AS0, AS1, AS2, AS3, AS4, AS5, AS6, AS7, AS8, AS9;
            public long AD0, AD1, AD2, AD3, AD4, AD5, AD6, AD7, AD8, AD9;
        }

        private struct CausalityInfo
        {
            public string Label;
            public int WindowMs;
            public bool Locked;
            public double Score;
        }

        private struct DomSnapshot
        {
            public long Seq;
            public DateTime ExchTs;
            public DateTime HostUtc;
            public double BidPx;
            public double AskPx;
            public long TouchBidSz;
            public long TouchAskSz;
            public long SumBidK;
            public long SumAskK;
            public double Pressure;
            public double Qimb;
            public long Ofi;
            public long DepthOfi;
        }

        private sealed class QueuedTrade
        {
            public DateTime ExchTs;
            public long ArrivalTicks;
            public double Price;
            public long Volume;
            public bool TrainEligible; // [PATCH 3/4]
        }

        private sealed class SpoofCandidate
        {
            public double Price;
            public long AddSize;
            public DateTime AddUtc;
        }

        private sealed class PendingTrade
        {
            public bool Active;
            public DateTime Utc;
            public double Price;
            public long Vol;
            public int Side;
        }

        private sealed class DomState
        {
            public int BarsIndex;

            public DomSnapshot[] HistoryBuffer = new DomSnapshot[HISTORY_BUFFER_SIZE];
            public int HistoryHead = 0; // Index where the NEXT item will be written
            public int HistoryCount = 0;
            public readonly object HistoryLock = new object();

            public LinkedList<QueuedTrade> TradeBuffer = new LinkedList<QueuedTrade>();

            public const int DEPTH = 10;
            public double[] BidPx = new double[DEPTH];
            public long[] BidSz = new long[DEPTH];
            public double[] AskPx = new double[DEPTH];
            public long[] AskSz = new long[DEPTH];

            public Dictionary<double, SpoofCandidate> SpoofBid = new Dictionary<double, SpoofCandidate>();
            public Dictionary<double, SpoofCandidate> SpoofAsk = new Dictionary<double, SpoofCandidate>();

            public PendingTrade Pending = new PendingTrade();

            public long Seq;
            public DateTime LastEventUtc;
            public long LastTouchBidSz;
            public long LastTouchAskSz;

            public double LastBidPx;
            public double LastAskPx;
            public long LastBidSize;
            public long LastAskSize;

            // [PATCH 3] Physics / High-Res Timing
            public long LastDomUpdateTicks; // Stopwatch.GetTimestamp()
            public double LastDomAgeMs;     // Calculated freshness

            // [PATCH 5] Price-Aware OFI Memory (Cont et al.)
            public double OfiPrevBidPx;
            public double OfiPrevAskPx;
            public long OfiPrevBidSz;
            public long OfiPrevAskSz;

            // [PATCH 1] Price-Aware OFI State
            public double PrevBestBid;
            public double PrevBestAsk;

            public double LastGoodBidPx;
            public double LastGoodAskPx;
            public int ValidBookCount;
            public long RepairedCrossed;

            public long TrainingCountDOM;
            public long TrainingCountTrade;
            public long ExecCountDOM;
            public long ExecCountTrade;

            public double LastKnownTradePx;
            public double DedupTradePx;
            public long DedupTradeVol;
            public DateTime DedupTradeTs;
            public DateTime LastTradeUtc;
            public DateTime LastDepthUpdateExchTs;

            // Audit
            public long ExchRegressCount;

            // Trackers
            public DateTime LastEmittedDomExchTs;
            public long LastEmittedDomSeq;
            public DateTime LastEmittedExchTs;
            public bool WasInSession;
            public bool IsDomBootstrapped;
            public bool IsMonitoring;
            public DateTime LastExchTs;
            public long LastInstSeq;

            public double CurrentBidPx;
            public double CurrentAskPx;
            public long CurrentTouchBid;
            public long CurrentTouchAsk;
            public long CurrentSumBid;
            public long CurrentSumAsk;

            public double LastWriteBidPx;
            public double LastWriteAskPx;
            public long LastWriteTouchBid;
            public long LastWriteTouchAsk;
            public long LastWriteSumBid;
            public long LastWriteSumAsk;
            public DateTime LastWriteUtc;
            public DateTime LastTopChangeUtc;
            public double LastStableMs;

            // --- Realized Volatility ---
            public double LastMidPx;
            public double RvAcc1s, RvAcc5s, RvAcc30s;
            public DateTime RvLastReset1s, RvLastReset5s, RvLastReset30s;
            public int RvCnt1s, RvCnt5s, RvCnt30s;
            public double QueuePosBid;
            public double QueuePosAsk;
            public double OfiMean;
            public double OfiVar;
            public int OfiCnt;
            public DateTime OfiStatResetTs;
            // --- Patch A: Kinematics Ticks ---
            public double MidVelTicks;
            public double MidAccelTicks;
            // --- Patch B: Top Flip ---
            public int TopFlipCount1s;
            public DateTime TopFlipReset1s;
            // --- Patch C: Spread Vel ---
            public double SpreadVel;
            public int LastSpreadTicks;
            // --- Patch D: OFI Skew ---
            public double OfiSkewEma;
            // --- Patch E: Cancel Ratio ---
            public double CancelAddRatio1s;

            // --- ML Feature: Price Kinematics ---
            public double MidVel;        // price units per ms
            public double MidAccel;      // price units per ms^2
            public DateTime LastMidTs;

            // --- ML Feature: Signed Trade Imbalance ---
            public long TradeImb1s;
            public DateTime TradeImbReset1s;

            // [PATCH 9] Session VWAP
            public long SessionVol;
            public double SessionPV;
            public double SessionVWAP;

            // [PATCH 8] EMA Fields (replacing resets)
            public double TradeImbEma;
            public double RvVarEma;
            public double RvVarEma5s;
            public double RvVarEma30s;
            public double PressureEma;
            public double FlipIntensityEma;
            public double SpreadVelEma;

            // --- ML Feature: Queue Refill Speed ---
            public double BidRefillRate;
            public double AskRefillRate;
            public DateTime LastRefillTs;

            // --- Feature 2: Time-Since-Event ---
            public DateTime LastTradeTs;
            public DateTime LastTopChangeTs;
            public DateTime LastSpreadChangeTs;
            public int SpreadTicks;

            // --- Feature 3: Slope ---
            public DateTime LastSlopeTs;

            // --- Feature 4: Event Density ---
            public int DomCnt100ms;
            public int TradeCnt100ms;
            public int CancelCnt100ms;
            public DateTime DensityResetTs;

            // [PATCH 6] Latency Stats
            public double LatencyP95;

            // [PATCH 8] Structural Drops Rate
            public int StructuralDrops1s;
            public DateTime StructuralDropsResetTs;

            // [PATCH 10] Burst Detection (NEW)
            public bool InBurst = false;
            public long LastBurstTs = 0;

            public long BurstId;
            public int BurstTradeCount;
            public DateTime LastBurstTime;

            // ---- Sweep tracking ----
            public double BurstMinPx = double.MaxValue;
            public double BurstMaxPx = double.MinValue;

            public double CurrentPressure;
            public double CurrentQimb;
            public double CurrentMicro;
            public long CurrentOfi;
            public long CurrentDepthOfi;
            public long CurrentChurnAdded;
            public long CurrentChurnCanceled;
            public double CurrentConcBid;
            public double CurrentConcAsk;
            public bool CurrentVoidBid;
            public bool CurrentVoidAsk;
            public int CurrentGapBid;
            public int CurrentGapAsk;

            public double LastActivePressure;
            public double PrevActivePressure;
            public double LastActiveQimb;
            public double PrevActiveQimb;
            public int LastSpoofFlag;
            public int LastIcebergFlag;

            public int PendingSpoofFlag;
            public int PendingIcebergFlag;

            public StreamWriter Writer;
            public StringBuilder Buffer = new StringBuilder(524288);

        public readonly object Sync = new object();
            public DateTime LastSnapshotUtc = DateTime.MinValue;
            public DateTime LastHeartbeatUtc = DateTime.MinValue;

            public long WrittenSnapshots;
            public long RejectedCrossed;

            public DateTime LastFlushUtc;
            public int UnflushedRows;

            public ConcurrentQueue<LightDomPayload> SnapshotQueue;
            public Task WriteTask;
            public CancellationTokenSource WriteCts;

            public string Symbol;
            public string Folder;
            public string Date;
            public string SessionDate; // frozen YYYYMMDD

            public string SessionId;

            // Audit Counters
            public long SchemaDropCount;
            public long DeferredTrades;
            public long ExchTsBackfillCount;
            public long SeqBreaks;
            public long RowsAttempted;
            public long RowsWritten;
            public long RowsRejected;
            public long RowsDropped;

            // Tier-1 Drop Taxonomy
            public long DropsPolicyLatency;
            public long DropsPolicySpread;
            public long DropsPolicyInterval;
            public long DropsHWM;
            public long DropsStructural;
            public long SessionDrops;

            // Latency Stats (Production Gold)
            // [PATCH 5] Extended Histogram for WAN tail
            public int[] LatencyHistogram = new int[2001]; // 0-2000ms
            public long LatencyCount;
            public long LatencyMax;

            // Circuit Breaker
            public int ConsecutiveIoErrors;
            public bool CircuitBreakerTripped;
            public bool BackpressureTripped;


            // [PATCH 3] CME Deduplication
            public DateTime LastTradeExchTs;
            public double LastTradePrice;

            // [FIX] SINGLE SOURCE OF TRUTH FOR HOST TIME
            public DateTime NextHostUtc()
            {
                // Delegated to global clock for FUSED consistency
                return NinjaTrader.NinjaScript.Indicators.A5_DOM_MultiDepth_AI_v309_ML_LOCKED.NextGlobalUtc();
            }
        }

        private DomState[] dom;

        // -------------------------------------------------------------------------------------
        // PATCH 1: GLOBAL SEQ ALLOCATOR (Cross-Instrument)
        // Never reset, never thread-local. Guarantees true monotonicity for fusion.
        // -------------------------------------------------------------------------------------
        // [FIX 1] Global Latch for Data Validity
        private static volatile bool bookReady = false;

        private static long GlobalSeq = 0;

        private SessionIterator[] sessionIterators;

        // -------------------------------------------------------------------------------------
        // PATCH 4: REFERENCE-COUNTED LIFECYCLE
        // Safe multi-chart startup/shutdown for the static writer
        // -------------------------------------------------------------------------------------
        private static readonly object fusedSync = new object();
        private static int fusedInstanceCount = 0; // Ref Counter
        private static string fusedDate;
        private static StreamWriter fusedWriter;

        // -------------------------------------------------------------------------------------
        // PATCH 4: Ring Buffer (Zero GC, Deterministic Consumer)
        // -------------------------------------------------------------------------------------
        private const int RING_SIZE = 262144; // 2^18 (Patch 6)
        private const int MASK = RING_SIZE - 1;

        private struct FusedEntry
        {
            public string Json;
            public long Seq;
            public long LocalTicks;
        }

        private static FusedEntry[] ring = new FusedEntry[RING_SIZE];
        // Note: GlobalSeq acts as the write index (mapped by seq)
        private static long fusedReadSeq = 1; // Start at 1 to match GlobalSeq start

        // FUSED writer recovery + liveness
        private static long fusedLastHeartbeatTicks = 0;
        private static long fusedLastWriteTicks = 0;
        // fusedGapSkips is already defined below

        private static readonly long TICKS_PER_SEC = Stopwatch.Frequency;
        private static readonly long FUSED_STALL_TICKS = TICKS_PER_SEC * 5;

        private static int fusedWriterRunning = 0; // atomic
        private static CancellationTokenSource fusedWriterCts;
        private static Task fusedWriterTask;

        private static readonly object fusedRestartGate = new object();

        // -------------------------------------------------------------------------------------
        // PATCH 7: EXPLICIT AUDIT COUNTERS
        // -------------------------------------------------------------------------------------
        private static long fusedDrops = 0;
        public static long DropsSeq = 0;
        private static long fusedGuardDrops = 0; // Audit for monotonicity enforcement

        // v308.1 HOTPATCH
        private static long fusedGapSkips = 0;
        private static DateTime lastWriterProgressUtc = DateTime.UtcNow;
        private static readonly TimeSpan WRITER_STALL_TIMEOUT = TimeSpan.FromSeconds(10);
        private static ManualResetEventSlim fusedWakeEvent = new ManualResetEventSlim(false);

        // [FIX] Global Monotonic Clock
        // [FIX] Global Monotonic Clock
        private static readonly object GlobalClockLock = new object();
        private static DateTime GlobalLastUtc = DateTime.MinValue;
        private static long MaxRingLag = 0;
        private static int FusedPart = 1;

        public static DateTime NextGlobalUtc()
        {
            lock (GlobalClockLock)
            {
                var now = DateTime.UtcNow;
                if (now <= GlobalLastUtc)
                    now = GlobalLastUtc.AddTicks(1);

                GlobalLastUtc = now;
                return now;
            }
        }

        private Task heartbeatTask;
        private CancellationTokenSource heartbeatCts;

        // -------------------------------------------------------------------------------------
        // PATCH 8: CONTROL EVENTS SEQUENCING
        // Allocates GlobalSeq for control messages so they don't float in time
        // -------------------------------------------------------------------------------------
        private void EmitControlEvent(string type, string reason)
        {
            // PATCH 1: Delay GlobalSeq Allocation Until After Repair Eligibility
            if (WriteFused)
            {
                if ((GlobalSeq - fusedReadSeq) >= RING_SIZE - 5000)
                {
                    Interlocked.Increment(ref fusedDrops);
                    return;
                }

                long seq = Interlocked.Increment(ref GlobalSeq);

                string instName = (Instrument != null) ? Instrument.FullName : "RECORDER";
                var json = $"{{\"tag\":\"CONTROL\",\"inst\":\"{instName}\",\"type\":\"{type}\",\"reason\":\"{reason}\",\"global_seq\":{seq},\"ts\":\"{NextGlobalUtc():O}\"}}";

                // PATCH 3 & 4: Fused Ring Buffer
                ring[seq & MASK] = new FusedEntry {
                    Json = json + "\n",
                    Seq = seq,
                    LocalTicks = Stopwatch.GetTimestamp()
                };
            }
        }

        // ==========================================================
        // STATE
        // ==========================================================

        // PATCH 1: Lead-Lag Singleton Instance ID
        private Guid _instanceId;

        // ==========================================================
        // V308.1 FUSED STATE & GATES
        // ==========================================================
        private static volatile bool fusedWriterReady = false;
        private static DateTime fusedFirstWriteTs;
        private static volatile bool fusedHasWrittenData = false;
        private static DateTime lastFusedWriteTs;
        private static long fusedRowsWritten = 0;

        // Cross-Instrument Gate
        private static volatile bool esLive = false;
        private static volatile bool nqLive = false;

        // Heartbeat
        private static DateTime lastFusedHeartbeat = DateTime.MinValue;
        public static long LatestEsSeq = 0;
        public static long LatestNqSeq = 0;

        // Daily Completeness
        private static long esRows = 0;
        private static long nqRows = 0;

        private static int fatalErrorCount = 0;

        private static string BuildFusedSessionStart()
        {
            return string.Format("{{\"tag\":\"CONTROL\",\"type\":\"fused_session_start\",\"ts\":\"{0:O}\",\"instruments\":[\"ES\",\"NQ\"],\"recorder_version\":\"{1}\"}}", NextGlobalUtc(), VERSION);
        }

        private static string BuildFusedHeartbeat()
        {
            return string.Format("{{\"tag\":\"CONTROL\",\"type\":\"fused_heartbeat\",\"ts\":\"{0:O}\",\"es_seq\":{1},\"nq_seq\":{2},\"global_seq\":{3},\"rows_written\":{4}}}", NextGlobalUtc(), LatestEsSeq, LatestNqSeq, GlobalSeq, fusedRowsWritten);
        }

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate)
        {
            if (connectionStatusUpdate.Status == ConnectionStatus.Connected)
            {
                fusedWakeEvent.Set();
            }
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = VERSION;
                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                DisplayInDataBox = false;
                IsSuspendedWhileInactive = true;
            }
            else if (State == State.Configure)
            {
                // DEBUG PRINT 1
                NinjaTrader.Code.Output.Process("[A5] State.Configure Started...", PrintTo.OutputTab1);

                _instanceId = Guid.NewGuid();

                // Static Reset Safety
                lock (fusedSync)
                {
                    // If we are configuring and dom is null, we assume a fresh start.
                    // We avoid resetting fusedWriterRunning blindly to respect reference counting,
                    // but we ensure gates are open.
                }

                if (!string.IsNullOrEmpty(SecondaryInstrument))
                {
                    try
                    {
                        AddDataSeries(SecondaryInstrument, BarsPeriodType.Tick, 1);
                    }
                    catch (Exception ex)
                    {
                        NinjaTrader.Code.Output.Process($"[A5] Failed to add secondary instrument '{SecondaryInstrument}': {ex.Message}", PrintTo.OutputTab1);
                    }
                }
            }
            else if (State == State.Realtime)
            {
                // DEBUG PRINT 2
                NinjaTrader.Code.Output.Process("[A5] State.Realtime Reached.", PrintTo.OutputTab1);

                A5UnifiedLeadLagEngineV309.Reset();
                sessionStartUtc = NextGlobalUtc();
                warmup = true;

                // -----------------------------------------------------------
                // PREVIOUS BUG FIX: "Late Join" check is removed here.
                // We no longer force WriteFused = false if started mid-session.
                // -----------------------------------------------------------

                // FUSED Startup Assertion
                lock (fusedSync)
                {
                    if (WriteFused)
                    {
                        fusedWriterReady = true;
                        fusedFirstWriteTs = NextGlobalUtc();
                        NinjaTrader.Code.Output.Process("[A5] FUSED Writer marked READY in Realtime.", PrintTo.OutputTab1);
                    }
                }
            }
            else if (State == State.DataLoaded)
            {
                // DEBUG PRINT 3
                NinjaTrader.Code.Output.Process("[A5] State.DataLoaded Started (Creating Dirs)...", PrintTo.OutputTab1);

                sessionIterators = new SessionIterator[BarsArray.Length];
                for (int i = 0; i < BarsArray.Length; i++)
                    sessionIterators[i] = new SessionIterator(BarsArray[i]);

                dom = new DomState[BarsArray.Length];

                string baseDir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "A5_DOM_Data_v309");

                // 1. Create Base Directory
                if (!Directory.Exists(baseDir))
                {
                    Directory.CreateDirectory(baseDir);
                    NinjaTrader.Code.Output.Process($"[A5] Created Base Dir: {baseDir}", PrintTo.OutputTab1);
                }

                // 2. Create FUSED Directory
                string fusedDir = Path.Combine(baseDir, "FUSED");
                if (!Directory.Exists(fusedDir))
                {
                    Directory.CreateDirectory(fusedDir);
                    NinjaTrader.Code.Output.Process($"[A5] Created FUSED Dir: {fusedDir}", PrintTo.OutputTab1);
                }

                if (WriteFused && !Directory.Exists(fusedDir))
                    throw new Exception("FUSED directory missing — recorder aborting.");

                for (int i = 0; i < dom.Length; i++)
                {
                    string name = BarsArray[i].Instrument.FullName;
                    string safeName = name.Replace(" ", "_");

                    dom[i] = new DomState();
                    for (int k = 0; k < DomState.DEPTH; k++)
                    {
                        dom[i].BidPx[k] = double.NaN;
                        dom[i].AskPx[k] = double.NaN;
                    }

                    dom[i].BarsIndex = i;
                    dom[i].Symbol = safeName;
                    dom[i].SessionId = Guid.NewGuid().ToString("N");

                    // Filter: Only NQ/ES by default or explicit request.
                    if (name.StartsWith("NQ") || name.StartsWith("ES") || name.StartsWith("MNQ") || name.StartsWith("MES"))
                    {
                        dom[i].IsMonitoring = true;
                    }
                    else
                    {
                        dom[i].IsMonitoring = false;
                        continue;
                    }

                    dom[i].Folder = Path.Combine(baseDir, dom[i].Symbol);
                    EnsureWriter(dom[i]);

                    // Background Writer
                    var d = dom[i];
                    d.SnapshotQueue = new ConcurrentQueue<LightDomPayload>();
                    d.WriteCts = new CancellationTokenSource();
                    d.WriteTask = Task.Run(() =>
                    {
                        SpinWait spin = new SpinWait();
                        while (!d.WriteCts.Token.IsCancellationRequested)
                        {
                            bool work = false;
                            LightDomPayload p;
                            while (d.SnapshotQueue.TryDequeue(out p))
                            {
                                try
                                {
                                    if (p.Tag == Tags.SYS_ROTATE)
                                    {
                                        if (d.Writer != null)
                                        {
                                            if (d.Buffer.Length > 0) { d.Writer.Write(d.Buffer.ToString()); d.Writer.Flush(); d.Buffer.Clear(); }
                                            WriteLatencyStats(d);
                                            d.Writer.Close();
                                        }
                                        d.Date = p.Arg1.ToString(CultureInfo.InvariantCulture);
                                        d.Writer = new StreamWriter(new FileStream(Path.Combine(d.Folder, $"{d.Symbol}_{d.Date}.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read, 4096), new UTF8Encoding(false));
                                        WriteMetadata(d);
                                        continue;
                                    }

                                    // Assign GlobalSeq at final emission
                                    long assignedSeq = 0;

                                    // [FIX 3] GLOBALSEQ ASSIGNMENT (GATED)
                                    // Only assign valid sequence numbers if the book is actually ready.
                                    // This keeps the sequence monotonic starting from 1 with valid data.
                                    if (WriteFused && bookReady && d.ValidBookCount >= 100 && p.Tag != Tags.HEARTBEAT)
                                    {
                                        p.GlobalSeq = System.Threading.Interlocked.Increment(ref GlobalSeq);
                                    }
                                    else
                                    {
                                        p.GlobalSeq = 0;
                                    }

                                    // Format JSON
                                    d.Buffer.Clear(); // Reuse
                                    if (p.Tag == Tags.HEARTBEAT)
                                    {
                                        bool c = false;
                                        d.Buffer.Append('{');
                                        AppendKV(d.Buffer, ref c, "inst", d.Symbol, quote: true);
                                        AppendKV(d.Buffer, ref c, "ts", p.HostUtc.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), quote: true);
                                        AppendKV(d.Buffer, ref c, "global_seq", p.GlobalSeq);
                                        AppendKV(d.Buffer, ref c, "tag", "CONTROL", quote: true);
                                        AppendKV(d.Buffer, ref c, "type", "heartbeat", quote: true);
                                        AppendKV(d.Buffer, ref c, "rows_written", d.RowsWritten);
                                        AppendKV(d.Buffer, ref c, "rows_rejected", d.RowsRejected);
                                        AppendKV(d.Buffer, ref c, "rows_dropped", d.RowsDropped);
                                        AppendKV(d.Buffer, ref c, "rows_attempted", d.RowsAttempted);

                                        AppendKV(d.Buffer, ref c, "drops_hwm", d.DropsHWM);
                                        AppendKV(d.Buffer, ref c, "drops_policy_interval", d.DropsPolicyInterval);
                                        AppendKV(d.Buffer, ref c, "drops_policy_latency", d.DropsPolicyLatency);
                                        AppendKV(d.Buffer, ref c, "drops_policy_spread", d.DropsPolicySpread);
                                        AppendKV(d.Buffer, ref c, "drops_structural", d.DropsStructural);
                                        AppendKV(d.Buffer, ref c, "drops_session", d.SessionDrops);
                                        AppendKV(d.Buffer, ref c, "fused_drops", System.Threading.Interlocked.Read(ref fusedDrops));
                                        AppendKV(d.Buffer, ref c, "fused_guard_drops", System.Threading.Interlocked.Read(ref fusedGuardDrops));
                                        AppendKV(d.Buffer, ref c, "fused_gap_skips", System.Threading.Interlocked.Read(ref fusedGapSkips));

                                        d.Buffer.Append('}');
                                    }
                                    else
                                    {
                                        FormatPayload(d.Buffer, ref p, d.Symbol);
                                    }
                                    d.Buffer.Append('\n');
                                    string jsonLine = d.Buffer.ToString();

                                    // [FIX 1] RING INSERT GATE (MANDATORY)
                                    // Prevents bad data from poisoning the ring buffer
                                    if (WriteFused && bookReady && d.ValidBookCount >= 100 && p.Tag != Tags.HEARTBEAT)
                                    {
                                        ring[p.GlobalSeq & MASK] = new FusedEntry
                                        {
                                            Json = jsonLine,
                                            Seq = p.GlobalSeq,
                                            LocalTicks = Stopwatch.GetTimestamp()
                                        };
                                    }

                                    d.Writer.Write(jsonLine);

                                    d.UnflushedRows++;

                                    if (d.UnflushedRows >= 2000 || (d.NextHostUtc() - d.LastFlushUtc).TotalMilliseconds >= FlushMs)
                                    {
                                        d.Writer.Flush();
                                        d.LastFlushUtc = d.NextHostUtc();
                                        d.UnflushedRows = 0;
                                    }

                                    d.RowsWritten++;
                                    d.ConsecutiveIoErrors = 0; // Reset on success
                                }
                                catch (Exception ex)
                                {
                                    d.ConsecutiveIoErrors++;
                                    if (d.ConsecutiveIoErrors > 10 && !d.CircuitBreakerTripped)
                                    {
                                        d.CircuitBreakerTripped = true;
                                        NinjaTrader.Code.Output.Process($"[A5] CRITICAL IO FAILURE on {d.Symbol}. Recording Stopped. {ex.Message}", PrintTo.OutputTab1);
                                        d.WriteCts.Cancel();
                                    }
                                }
                                work = true;
                            }
                            if (d.UnflushedRows > 0)
                            {
                                d.Writer.Flush();
                                d.LastFlushUtc = d.NextHostUtc();
                                d.UnflushedRows = 0;
                            }
                            if (!work) spin.SpinOnce();
                            else spin.Reset();
                        }
                        // Drain
                        LightDomPayload finalP;
                        while (d.SnapshotQueue.TryDequeue(out finalP))
                        {
                            if (finalP.Tag != Tags.SYS_ROTATE)
                            {
                                d.Buffer.Clear();
                                FormatPayload(d.Buffer, ref finalP, d.Symbol);
                                d.Buffer.Append('\n');
                                d.Writer.Write(d.Buffer);
                            }
                        }
                        if (d.Writer != null) d.Writer.Flush();
                    });
                }

                heartbeatCts = new CancellationTokenSource();
                heartbeatTask = Task.Run(() => HeartbeatLoop());

                lock (fusedSync)
                {
                    if (WriteFused)
                    {
                        fusedInstanceCount++;
                        if (Interlocked.CompareExchange(ref fusedWriterRunning, 1, 0) == 0)
                        {
                            fusedWriterCts = new CancellationTokenSource();
                            fusedWriterTask = Task.Run(() => FusedWriterLoop(), fusedWriterCts.Token);
                            NinjaTrader.Code.Output.Process("[A5] FUSED Writer Background Task Started.", PrintTo.OutputTab1);
                        }
                    }
                }
            }
            else if (State == State.Terminated)
            {
                // ... (Keep your existing Terminated logic unchanged, or copy it back if needed) ...
                try
                {
                    if (fusedRowsWritten < Math.Min(esRows, nqRows) * 0.8)
                    {
                        NinjaTrader.Code.Output.Process("[WARN] FUSED row count below expected threshold", PrintTo.OutputTab1);
                    }

                    try
                    {
                        string baseDir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "A5_DOM_Data_v309", "FUSED");
                        string sessionDate = NextGlobalUtc().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(fusedDate)) sessionDate = fusedDate;

                        long totalDrops = 0;
                        long totalRegress = 0;
                        if (dom != null) {
                            foreach(var d in dom) {
                                if(d!=null) {
                                    totalDrops += d.DropsStructural;
                                    totalRegress += d.ExchRegressCount;
                                }
                            }
                        }

                        long uptime = (long)(NextGlobalUtc() - sessionStartUtc).TotalSeconds;

                        string auditJson = "{\n" +
                            $"  \"fused_started\": {(fusedWriterReady ? "true" : "false")},\n" +
                            $"  \"fused_rows\": {fusedRowsWritten},\n" +
                            $"  \"fatal_errors\": {fatalErrorCount},\n" +
                            $"  \"total_structural_drops\": {totalDrops},\n" +
                            $"  \"total_exch_regress_count\": {totalRegress},\n" +
                            $"  \"max_ring_lag\": {MaxRingLag},\n" +
                            $"  \"total_fused_gap_skips\": {Interlocked.Read(ref fusedGapSkips)},\n" +
                            $"  \"recorder_uptime_seconds\": {uptime},\n" +
                            $"  \"recorder_version\": \"{VERSION}\"\n" +
                            "}";

                        File.WriteAllText(
                            Path.Combine(baseDir, $"{sessionDate}.recorder_audit.json"),
                            auditJson
                        );
                    }
                    catch { }

                    if (dom != null)
                    {
                        foreach (var d in dom)
                        {
                            if (d == null || !d.IsMonitoring) continue;
                            if (d.WriteCts != null) { d.WriteCts.Cancel(); }

                            // Close individual writers
                            lock (d.Sync) {
                                if (d.Writer != null) {
                                    WriteLatencyStats(d);
                                    d.Writer.Close();
                                }
                            }
                        }
                    }

                    if (heartbeatCts != null) heartbeatCts.Cancel();

                    lock (fusedSync)
                    {
                        if (WriteFused)
                        {
                            fusedInstanceCount--;
                            if (fusedInstanceCount <= 0 && fusedWriterRunning != 0)
                            {
                                esLive = false;
                                nqLive = false;
                                fusedWriterReady = false;

                                if (fusedWriterCts != null) fusedWriterCts.Cancel();
                                try { fusedWriterTask.Wait(1000); } catch { }
                                fusedWriterRunning = 0;
                            }
                        }
                    }
                }
                catch { }
            }
        }


        // ==========================================================
        // FILE MANAGEMENT
        // ==========================================================

        private void EnsureWriter(DomState d)
        {
            if (d.Writer != null) return;
            if (!Directory.Exists(d.Folder)) Directory.CreateDirectory(d.Folder);

            string today = d.NextHostUtc().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            try { today = sessionIterators[0].GetTradingDay(d.NextHostUtc()).ToString("yyyyMMdd", CultureInfo.InvariantCulture); } catch {}

            d.SessionDate = today;
            d.Date = today;
            d.LastFlushUtc = d.NextHostUtc();
            d.DensityResetTs = d.NextHostUtc();
            // PATCH 6: Removed WriteThrough for performance - OS buffering is sufficient for HFT streams
            d.Writer = new StreamWriter(
                new FileStream(Path.Combine(d.Folder, $"{d.Symbol}_{d.Date}.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.Read, 4096),
                new UTF8Encoding(false));

            WriteMetadata(d);
        }

        private void WriteMetadata(DomState d)
        {
            string metaPath = Path.Combine(d.Folder, $"{d.Symbol}_{d.Date}.meta.json");

            string raw = VERSION + "v309_ml_locked" + MaxDepth + PressureLevels + d.SessionDate;
            string hash = "";
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2"));
                hash = builder.ToString();
            }

            string json =
                "{\n" +
                $"  \"instrument\": \"{d.Symbol}\",\n" +
                $"  \"session_hash\": \"{hash}\",\n" +
                $"  \"utc_date\": \"{d.SessionDate}\",\n" +
                $"  \"timezone\": \"UTC\",\n" +
                $"  \"recorder_version\": \"{VERSION}\",\n" +
                $"  \"feature_patch\": \"v309_ml_locked\",\n" +
                $"  \"wan_normalized\": true,\n" +
                $"  \"format\": \"jsonl\",\n" +
                $"  \"max_depth\": {MaxDepth},\n" +
                $"  \"pressure_levels\": {PressureLevels},\n" +
                $"  \"depth_ofi_levels\": {DepthOfiLevels},\n" +
                $"  \"queue_imb_levels\": {QueueImbLevels},\n" +
                $"  \"l1_queue_source\": \"touch_bidSz/touch_askSz\",\n" +
                $"  \"determinism\": {{ \"recorder_version\": \"{VERSION}\", \"repair_ruleset\": \"A5_REPAIR_V3\" }}\n" +
                "}";
            File.WriteAllText(metaPath, json, new UTF8Encoding(false));
        }

        private void RotateIfNeeded(int idx)
        {
            var d = dom[idx];
            string today = sessionIterators[idx].GetTradingDay(d.NextHostUtc()).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (today == d.Date) return;

            d.Date = today;
            long dateVal = 0;
            long.TryParse(today, out dateVal);

            // Enqueue Rotation Command
            var p = new LightDomPayload
            {
                Tag = Tags.SYS_ROTATE,
                Arg1 = dateVal
            };
            d.SnapshotQueue.Enqueue(p);

            d.Seq = 0;
            d.LastEmittedDomSeq = -1;
            d.LastEmittedExchTs = DateTime.MinValue;
            d.IsDomBootstrapped = false;
            d.LastInstSeq = 0;
        }

        // ==========================================================
        // MARKET DEPTH
        // ==========================================================

        private int GetPriceLevelIndex(double[] px, double price)
        {
            for (int i = 0; i < DomState.DEPTH; i++)
            {
                if (!double.IsNaN(px[i]) && Math.Abs(px[i] - price) < 1e-9)
                    return i;
            }
            return -1;
        }

        private void UpdateBook(DomState d, bool isBid, double price, long size)
        {
            var px = isBid ? d.BidPx : d.AskPx;
            var sz = isBid ? d.BidSz : d.AskSz;
            int limit = DomState.DEPTH;

            // ============================
            // 1. UPDATE / DELETE EXISTING
            // ============================

            for (int i = 0; i < limit; i++)
            {
                if (px[i] == price)
                {
                    if (size > 0)
                    {
                        sz[i] = size;
                        return;
                    }

                    // delete → shift LEFT
                    for (int j = i; j < limit - 1; j++)
                    {
                        px[j] = px[j + 1];
                        sz[j] = sz[j + 1];
                    }

                    px[limit - 1] = double.NaN;
                    sz[limit - 1] = 0;
                    return;
                }
            }

            if (size == 0) return;

            // ============================
            // 2. INSERT NEW (SORTED)
            // Bids: descending
            // Asks: ascending
            // ============================

            for (int i = 0; i < limit; i++)
            {
                bool empty = double.IsNaN(px[i]);
                bool insert = false;

                if (!empty)
                {
                    if (isBid) insert = price > px[i];
                    else       insert = price < px[i];
                }

                if (empty || insert)
                {
                    // shift RIGHT
                    for (int j = limit - 1; j > i; j--)
                    {
                        px[j] = px[j - 1];
                        sz[j] = sz[j - 1];
                    }

                    px[i] = price;
                    sz[i] = size;
                    return;
                }
            }

            // beyond depth → ignore
        }

        protected override void OnBarUpdate()
        {
            // 🔴 2️⃣ First-Row Deadline (Fail-Fast)
            #region FUSED First Row Deadline

            if (WriteFused && fusedWriterReady && !fusedHasWrittenData)
            {
                if ((DateTime.UtcNow - fusedFirstWriteTs).TotalMinutes > 5)
                {
                    NinjaTrader.Code.Output.Process("[WARN] FUSED quiet period exceeded 5 minutes (no activity yet)", PrintTo.OutputTab1);
                }
            }

            #endregion

            // 🟠 6️⃣ Writer Health Monitor (Check)
            // Added check for queue count to avoid stalling on empty market
            long now = Stopwatch.GetTimestamp();

            if (fusedWriterRunning != 0)
            {
                if (now - fusedLastHeartbeatTicks > FUSED_STALL_TICKS ||
                    now - fusedLastWriteTicks > FUSED_STALL_TICKS)
                {
                    NinjaTrader.Code.Output.Process("[A5] FUSED stall detected — restarting writer", PrintTo.OutputTab1);
                    TryRestartFusedWriter();
                }
            }
        }


        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            try
            {
                int idx = BarsInProgress;
                if (idx < 0 || idx >= dom.Length) return;
                var d = dom[idx];
                if (!d.IsMonitoring) return;

                lock (d.Sync)
                {
                    EnsureWriter(d);

                    // [FIX] SINGLE SOURCE OF TRUTH (Monotonic)
                    DateTime nowUtc = d.NextHostUtc();

                    // [FIX] Guard RotateIfNeeded: Only check session if bars exist
                    // This prevents "Series Out Of Range" error on Secondary Instrument startup
                    if (BarsArray[idx] != null && BarsArray[idx].Count > 0)
                    {
                        RotateIfNeeded(idx);
                    }

                    // [FIX] STRICT DROP on Time Regression (Do not Emit)
                    if (d.LastDepthUpdateExchTs > DateTime.MinValue && e.Time < d.LastDepthUpdateExchTs.AddMilliseconds(-120))
                    {
                        d.DropsStructural++;
                        return;
                    }

                    // [FIX 1 & 5] BOOK READY LATCH
                    double bestBid = GetBestBid(d);
                    double bestAsk = GetBestAsk(d);

                    // If bestBid is 0, we try to use the incoming event to seed it temporarily for the check
                    if (bestBid == 0 && e.MarketDataType == MarketDataType.Bid) bestBid = e.Price;
                    if (bestAsk == 0 && e.MarketDataType == MarketDataType.Ask) bestAsk = e.Price;

                    if (!bookReady &&
                        bestBid > 0 &&
                        bestAsk > 0 &&
                        bestAsk >= bestBid &&
                        e.Time.Year > 2000)
                    {
                        bookReady = true;
                        NinjaTrader.Code.Output.Process($"[A5] BOOK READY DETECTED ({d.Symbol}) - STREAM UNLOCKED", PrintTo.OutputTab1);

                        // [FIX 5] RESET ML ACCUMULATORS
                        d.OfiSkewEma = 0;
                        d.PressureEma = 0;
                        d.TradeImbEma = 0;
                        d.SpreadVelEma = 0;
                        d.FlipIntensityEma = 0;
                        d.RvVarEma = 0; d.RvVarEma5s = 0; d.RvVarEma30s = 0;
                        d.LastMidPx = 0;
                    }

                    // Re-fetch strict best bid/ask for logic (ignore temp seed)
                    bestBid = GetBestBid(d);
                    bestAsk = GetBestAsk(d);

                    if (bestBid > 0) d.LastBidPx = bestBid;
                    if (bestAsk > 0) d.LastAskPx = bestAsk;

                    // HARD WARMUP EXIT MOVED TO EmitRow

                    bool forceGapFill = false;
                    if (d.LastEmittedDomExchTs > DateTime.MinValue && (e.Time - d.LastEmittedDomExchTs).TotalMilliseconds > 1000)
                        forceGapFill = true;

                    bool isBid = e.MarketDataType == MarketDataType.Bid;
                    long oldVol = GetLevelSize(d, isBid, e.Price);

                    var levelPx = isBid ? d.BidPx : d.AskPx;
                    int prevLevelIndex = GetPriceLevelIndex(levelPx, e.Price);

                    UpdateBook(d, isBid, e.Price, e.Volume);

                    long newVol = GetLevelSize(d, isBid, e.Price);
                    long delta = newVol - oldVol;

                    int currLevelIndex = GetPriceLevelIndex(levelPx, e.Price);
                    int liqLevel = currLevelIndex != -1 ? currLevelIndex : prevLevelIndex;
                    int liqSide = isBid ? 1 : -1;

                    // CRITICAL: Re-fetch best bid/ask AFTER update so downstream logic (OFI, etc) is correct
                    bestBid = GetBestBid(d);
                    bestAsk = GetBestAsk(d);

                    if (bestBid > 0) d.LastBidPx = bestBid;
                    if (bestAsk > 0) d.LastAskPx = bestAsk;

                    lock (d.HistoryLock)
                    {
                        int oldestIdx = (d.HistoryCount < HISTORY_BUFFER_SIZE) ? 0 : d.HistoryHead;
                        // Ring buffer overwrite logic is implicit via head/count
                    }

                    int kP = ClampK(PressureLevels, 1, DomState.DEPTH);
                    long sB = SumTop(d, isBid: true, k: kP);
                    long sA = SumTop(d, isBid: false, k: kP);
                    long tB = GetLevelSize(d, isBid: true, bestBid);
                    long tA = GetLevelSize(d, isBid: false, bestAsk);

                    d.LastBidSize = tB;
                    d.LastAskSize = tA;
                    if (d.LastBidSize < 0) d.LastBidSize = 0;
                    if (d.LastAskSize < 0) d.LastAskSize = 0;

                    double histBid = bestBid;
                    double histAsk = bestAsk;

                    if (bestBid > 0 && bestAsk > 0 && bestBid >= bestAsk)
                    {
                        if (d.LastGoodBidPx > 0 && d.LastGoodAskPx > d.LastGoodBidPx)
                        {
                            histBid = d.LastGoodBidPx;
                            histAsk = d.LastGoodAskPx;
                        }
                    }

                    // [FIX] Use monotonic 'nowUtc' for Spoof/Iceberg/Features
                    if (EnableSpoof)
                        UpdateSpoof(d, nowUtc, isBid, e.Price, delta, bestBid, bestAsk);

                    if (EnableIceberg)
                        UpdateIcebergFromDepth(d, nowUtc, isBid ? 1 : -1, e.Price, delta);

                    // OFI Logic
                    if (Math.Abs(bestBid - d.OfiPrevBidPx) > 1e-9 || Math.Abs(bestAsk - d.OfiPrevAskPx) > 1e-9)
                    {
                        d.OfiCnt = 0; d.OfiMean = 0; d.OfiVar = 0; d.OfiStatResetTs = nowUtc;
                    }

                    long flowBid = 0;
                    if (d.OfiPrevBidPx == 0) flowBid = 0;
                    else if (bestBid > d.OfiPrevBidPx) flowBid = tB;
                    else if (bestBid < d.OfiPrevBidPx) flowBid = -d.OfiPrevBidSz;
                    else flowBid = tB - d.OfiPrevBidSz;

                    long flowAsk = 0;
                    if (d.OfiPrevAskPx == 0) flowAsk = 0;
                    else if (bestAsk < d.OfiPrevAskPx) flowAsk = tA;
                    else if (bestAsk > d.OfiPrevAskPx) flowAsk = -d.OfiPrevAskSz;
                    else flowAsk = tA - d.OfiPrevAskSz;

                    long currentOfi = flowBid - flowAsk;
                    long currentDepthOfi = 0;

                    d.OfiPrevBidPx = bestBid; d.OfiPrevAskPx = bestAsk;
                    d.OfiPrevBidSz = tB; d.OfiPrevAskSz = tA;
                    d.LastDomUpdateTicks = Stopwatch.GetTimestamp();

                    lock (d.HistoryLock)
                    {
                        d.HistoryBuffer[d.HistoryHead] = new DomSnapshot
                        {
                            Seq = d.Seq, ExchTs = e.Time, HostUtc = nowUtc,
                            BidPx = histBid, AskPx = histAsk, TouchBidSz = tB, TouchAskSz = tA,
                            SumBidK = sB, SumAskK = sA, Pressure = RatioSigned(sB, sA),
                            Qimb = Ratio01(sB, sA), Ofi = currentOfi, DepthOfi = currentDepthOfi
                        };
                        d.HistoryHead = (d.HistoryHead + 1) % HISTORY_BUFFER_SIZE;
                        if (d.HistoryCount < HISTORY_BUFFER_SIZE) d.HistoryCount++;
                    }

                    // [FIX] Use monotonic 'nowUtc' for feature updates
                    if (!d.InBurst)
                    {
                        UpdateFeatureState(d, nowUtc, bestBid, bestAsk, tB, tA, sB, sA, currentOfi, currentDepthOfi);
                    }
                    else
                    {
                        // Burst reset logic...
                    }

                    if (UseSessionFilter && !sessionIterators[idx].IsInSession(nowUtc, true, true))
                    {
                        d.SessionDrops++; d.RowsDropped++; return;
                    }

                    bool inSession = sessionIterators[idx].IsInSession(e.Time, true, true);
                    bool forceSession = false;
                    if (inSession != d.WasInSession)
                    {
                        forceSession = true; d.WasInSession = inSession;
                    }

                    // ================================
                    // CME BURST DETECTION
                    // ================================
                    long deltaExchMs = 0;
                    if (d.LastDepthUpdateExchTs > DateTime.MinValue)
                        deltaExchMs = (long)(e.Time - d.LastDepthUpdateExchTs).TotalMilliseconds;

                    if (deltaExchMs > 250)
                    {
                        d.InBurst = true;
                        d.LastBurstTs = Stopwatch.GetTimestamp();
                    }
                    else if (d.InBurst)
                    {
                        // Clear burst after 100ms of clean flow
                        if ((Stopwatch.GetTimestamp() - d.LastBurstTs) > Stopwatch.Frequency * 0.1)
                            d.InBurst = false;
                    }

                    d.LastDepthUpdateExchTs = e.Time;
                    ProcessPendingTrades(idx);

                    byte liqEvent = LiqEvents.MODIFY;
                    if (delta > 0) liqEvent = LiqEvents.ADD;
                    else if (delta < 0) liqEvent = LiqEvents.CANCEL;

                    byte forceReason = Reasons.NONE;
                    if (forceSession || forceGapFill) forceReason = Reasons.GAP_FILL;

                    EmitRow(d, e.Time, Tags.DOM, 0, e.Price, liqEvent, CausalLabels.NONE, forceSession || forceGapFill, forceReason, liqSide, liqLevel, delta, null, sB, sA, currentOfi, currentDepthOfi);
                }
            }
            catch (Exception ex)
            {
                // This catch block silences the "Series Out Of Range" error during startup
                // It's safe because it only happens when secondary data is missing, which means we can't record it anyway.
                if (!ex.Message.Contains("accessing a series"))
                {
                    NinjaTrader.Code.Output.Process($"[A5] DOM Error: {ex.Message}", PrintTo.OutputTab1);
                }
            }
        }

        // ==========================================================
        // MARKET DATA (TRADES + QUOTES)
        // ==========================================================
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            int idx = BarsInProgress;
            if (idx < 0 || idx >= dom.Length) return;
            var d = dom[idx];
            if (!d.IsMonitoring) return;

            lock (d.Sync)
            {
                EnsureWriter(d);

                // [FIX] SINGLE SOURCE OF TRUTH
                DateTime nowUtc = d.NextHostUtc();

                if (e.MarketDataType == MarketDataType.Bid)
                {
                    if (e.Price > 0) d.LastBidPx = e.Price;
                    return;
                }
                if (e.MarketDataType == MarketDataType.Ask)
                {
                    if (e.Price > 0) d.LastAskPx = e.Price;
                    return;
                }

                if (e.MarketDataType != MarketDataType.Last) return;

                // [PATCH 3] CME Trade Deduplication
                if (e.Time == d.LastTradeExchTs && e.Price == d.LastTradePrice)
                    return;

                d.LastTradeExchTs = e.Time;
                d.LastTradePrice = e.Price;

                if (Math.Abs(e.Price - d.DedupTradePx) < 0.0000001 &&
                    e.Volume == d.DedupTradeVol &&
                    e.Time == d.DedupTradeTs)
                {
                    return;
                }


                // ==============================================================================
                // PATCH 3 & 4: PHYSICS GUARD & CAUSAL INTEGRITY
                // Stops "Hallucination Engine" by dropping temporally/spatially impossible trades
                // ==============================================================================

                // 1. Freshness Check (High-Resolution)
                long tickDelta = Stopwatch.GetTimestamp() - d.LastDomUpdateTicks;
                double domAgeMs = (double)tickDelta * 1000.0 / Stopwatch.Frequency;

                // [CRITICAL] If DOM is stale > 10ms (WAN) or 2ms (Colo), the trade implies
                // a state we haven't received yet. Recording this creates "Lookahead Bias".
                // [PATCH] Adjusted for 75ms WAN Latency
                if (domAgeMs > 100.0)
                {
                    // Optional: Log/Count the drop
                    d.DropsStructural++;
                    d.StructuralDrops1s++;
                    return; // HARD REJECT
                }

                // 2. Spatial Causal Check (Price Alignment)
                // A trade at 5000.00 cannot happen if BestBid is 4000.00.
                // NinjaTrader async feeds often desync by 100+ ms.
                bool causalMatch = false;

                // Check Bid Side (Sell order hitting bid)
                if (d.LastBidPx > 0 && e.Price <= d.LastBidPx + (TickSize * 0.5)) causalMatch = true;

                // Check Ask Side (Buy order lifting offer)
                if (d.LastAskPx > 0 && e.Price >= d.LastAskPx - (TickSize * 0.5)) causalMatch = true;

                // Allow "Through" trades (sweeps) within minimal tolerance
                double driftTicks = 0;
                if (!causalMatch)
                {
                    // Calculate drift
                    driftTicks = Math.Min(Math.Abs(e.Price - d.LastBidPx), Math.Abs(e.Price - d.LastAskPx)) / TickSize;

                    // [AUDIT RULE] If drift > 2 ticks, it is a phantom trade from a previous/future state.
                    if (driftTicks > 2)
                    {
                        d.DropsStructural++;
                        d.StructuralDrops1s++;
                        return; // HARD REJECT
                    }
                }

                // 3. Mark Eligibility for ML
                // Only set 'true' if perfectly aligned
                // [PATCH] Allow standard WAN jitter
                bool trainEligible = (domAgeMs < 25.0) && (driftTicks < 1.0);
                d.DedupTradePx = e.Price;
                d.DedupTradeVol = (long)e.Volume;
                d.DedupTradeTs = e.Time;
                d.LastKnownTradePx = e.Price;
                // d.LastTradeUtc = DateTime.UtcNow; // Removed per Patch 1

                RotateIfNeeded(idx);

                // HARD WARMUP EXIT MOVED TO EmitRow

                int side = 0;
                double bid = d.LastBidPx;
                double ask = d.LastAskPx;

                if (bid > 0 && ask > 0)
                {
                    if (e.Price >= ask - (TickSize * 0.00001)) side = +1;
                    else if (e.Price <= bid + (TickSize * 0.00001)) side = -1;
                    else side = 0;
                }

                if (EnableIceberg && e.Volume >= IcebergMinTradeVol)
                {
                    d.Pending.Active = true;
                    d.Pending.Utc = nowUtc; // FIXED
                    d.Pending.Price = e.Price;
                    d.Pending.Vol = (long)e.Volume;
                    d.Pending.Side = side;
                }

                if (UseSessionFilter && !sessionIterators[idx].IsInSession(nowUtc, true, true))
                {
                    d.SessionDrops++;
                    d.RowsDropped++;
                    return;
                }

                // [FIX] Update features with monotonic time
                // Note: Trade features usually update on 'EmitRow' internal logic,
                // but if you update any state here, use 'nowUtc'.

                EmitRow(d, e.Time, Tags.TRADE, (long)e.Volume, e.Price, LiqEvents.NONE, CausalLabels.NONE);
            }
        }

        // ==========================================================
        // TRADE PROCESSING (BOUNDED WAIT)
        // ==========================================================
        private void ProcessPendingTrades(int idx)
        {
            var d = dom[idx];
            if (d.TradeBuffer.Count == 0) return;

            int removeCount = 0;
            var node = d.TradeBuffer.First;
            while(node != null)
            {
                var trade = node.Value;
                bool causalFound = false;
                lock (d.HistoryLock)
                {
                    if (d.HistoryCount > 0)
                    {
                        int lastIdx = (d.HistoryHead - 1 + HISTORY_BUFFER_SIZE) % HISTORY_BUFFER_SIZE;
                        if (d.HistoryBuffer[lastIdx].ExchTs >= trade.ExchTs)
                            causalFound = true;
                    }
                }

                double waitMs = (Stopwatch.GetTimestamp() - trade.ArrivalTicks) * 1000.0 / Stopwatch.Frequency;
                bool isTimeout = waitMs > 500;

                if (causalFound || isTimeout)
                {
                    byte label = causalFound ? CausalLabels.PRE_TRADE : CausalLabels.TIMEOUT;
                    // REMOVED arrivalTime, ADDED CausalLabels.NONE
                    EmitRow(d, trade.ExchTs, Tags.TRADE, trade.Volume, trade.Price, LiqEvents.NONE, label, trainEligibleOverride: trade.TrainEligible);
                    removeCount++;
                }
                else
                {
                    break;
                }
                node = node.Next;
            }

            while(removeCount > 0)
            {
                d.TradeBuffer.RemoveFirst();
                removeCount--;
            }
        }

        private void UpdateFeatureState(DomState d, DateTime nowUtc, double bid, double ask, long tB, long tA, long sB, long sA, long currentOfi, long currentDepthOfi)
        {
            d.CurrentBidPx = bid;
            d.CurrentAskPx = ask;
            d.CurrentTouchBid = tB;
            d.CurrentTouchAsk = tA;
            d.CurrentSumBid = sB;
            d.CurrentSumAsk = sA;

            // --- Feature 1: Queue Position ---
            d.QueuePosBid = ComputeQueuePos(d, true, bid, d.CurrentTouchBid, 10);
            d.QueuePosAsk = ComputeQueuePos(d, false, ask, d.CurrentTouchAsk, 10);

            // --- Feature 3: OFI Shock Stats (Welford) ---
            if (d.OfiStatResetTs == DateTime.MinValue ||
                (nowUtc - d.OfiStatResetTs).TotalSeconds >= 5)
            {
                d.OfiCnt = 0;
                d.OfiMean = 0;
                d.OfiVar = 0;
                d.OfiStatResetTs = nowUtc;
            }

            d.OfiCnt++;
            double delta = currentOfi - d.OfiMean;
            d.OfiMean += delta / d.OfiCnt;
            d.OfiVar  += delta * (currentOfi - d.OfiMean);

            // --- Patch D: OFI Skew EMA ---
            double alphaSkew = 0.2;
            if (d.OfiCnt < 5) d.OfiSkewEma = 0;
            d.OfiSkewEma = alphaSkew * Math.Sign(currentOfi) + (1.0 - alphaSkew) * d.OfiSkewEma;

            // =============================
            // ML Feature: Queue Refill Speed
            // =============================
            if (d.LastRefillTs != DateTime.MinValue)
            {
                double dt = (nowUtc - d.LastRefillTs).TotalMilliseconds;
                // [PATCH 4] Clamp dt floor (WAN Safe)
                dt = Math.Max(dt, 5.0);

                if (dt >= 20 && dt <= 500)
                {
                    d.BidRefillRate = (d.CurrentTouchBid - d.LastTouchBidSz) / dt;
                    d.AskRefillRate = (d.CurrentTouchAsk - d.LastTouchAskSz) / dt;
                }
            }

            d.LastTouchBidSz = d.CurrentTouchBid;
            d.LastTouchAskSz = d.CurrentTouchAsk;
            d.LastRefillTs = nowUtc;

            int kQi = ClampK(QueueImbLevels, 1, DomState.DEPTH);
            long sumBidQi = SumTop(d, isBid: true, k: kQi);
            long sumAskQi = SumTop(d, isBid: false, k: kQi);

            d.PrevActivePressure = d.LastActivePressure;
            d.LastActivePressure = RatioSigned(sB, sA);

            // [PATCH 8] Pressure EMA handled in EmitRow via continuous decay

            d.PrevActiveQimb = d.LastActiveQimb;
            d.LastActiveQimb = Ratio01(sumBidQi, sumAskQi);

            d.CurrentOfi = currentOfi;
            d.CurrentDepthOfi = currentDepthOfi;

            long added = 0;
            long canceled = 0;
            // ComputeChurn disabled
            d.CurrentChurnAdded = added;
            d.CurrentChurnCanceled = canceled;

            // --- Patch E: Cancel/Add Ratio ---
            long churnTot = d.CurrentChurnAdded + d.CurrentChurnCanceled;
            d.CancelAddRatio1s = churnTot > 0 ? (double)d.CurrentChurnCanceled / churnTot : 0.0;

             d.CurrentMicro = (tB + tA) > 0
                ? (ask * tB + bid * tA) / (double)(tB + tA)
                : 0.0;

            d.CurrentConcBid = (sB > 0) ? (double)tB / sB : 0.0;
            d.CurrentConcAsk = (sA > 0) ? (double)tA / sA : 0.0;

            d.CurrentVoidBid = sB < 10;
            d.CurrentVoidAsk = sA < 10;

            d.CurrentGapBid = 0;
            if (!double.IsNaN(d.BidPx[0]) && !double.IsNaN(d.BidPx[1])) {
                 d.CurrentGapBid = (int)Math.Round(Math.Abs(d.BidPx[0] - d.BidPx[1]) / TickSize);
            }
            d.CurrentGapAsk = 0;
            if (!double.IsNaN(d.AskPx[0]) && !double.IsNaN(d.AskPx[1])) {
                 d.CurrentGapAsk = (int)Math.Round(Math.Abs(d.AskPx[0] - d.AskPx[1]) / TickSize);
            }

            d.PendingSpoofFlag = d.LastSpoofFlag;
            d.PendingIcebergFlag = d.LastIcebergFlag;
            d.LastSpoofFlag = 0;
            d.LastIcebergFlag = 0;
        }

        // [PATCH 9, 10, 11] Telemetry & Priority Logic
        private static long RingHighWater = 0; // Global for Watermark

        private static void FusedWriterLoop()
        {
            // Patch 11: Priority
            Thread.CurrentThread.Priority = ThreadPriority.Highest;

            fusedLastHeartbeatTicks = Stopwatch.GetTimestamp();
            fusedLastWriteTicks = fusedLastHeartbeatTicks;

            if (fusedReadSeq == 0) fusedReadSeq = 1;

            long flushIntervalTicks = (long)(0.050 * Stopwatch.Frequency); // Relaxed to 50ms
            long lastDiskFlushTicks = Stopwatch.GetTimestamp();

            // Telemetry Timers
            long lastLoopTicks = Stopwatch.GetTimestamp();
            long lastWatermarkTicks = Stopwatch.GetTimestamp();
            long ticksPerMs = Stopwatch.Frequency / 1000;

            SpinWait spin = new SpinWait();

            while (!fusedWriterCts.IsCancellationRequested)
            {
                try
                {
                    long now = Stopwatch.GetTimestamp();

                    // Patch 9: Stall Detector & RECOVERY
                    long deltaMs = (now - lastLoopTicks) / ticksPerMs;
                    if (deltaMs > 10)
                    {
                        // We use Try-Catch here to ensure telemetry never crashes the writer
                        try {
                             if (fusedWriter != null)
                                fusedWriter.WriteLine($"{{\"tag\":\"SYSTEM_STALL\",\"ts\":\"{NextGlobalUtc():O}\",\"stall_ms\":{deltaMs}}}");
                        } catch {}

                        // [PHASE 2] HARD RESTART (>5s)
                        if (deltaMs > 5000)
                        {
                            try {
                                if (fusedWriter != null) {
                                     fusedWriter.Flush();
                                     fusedWriter.Close();
                                }
                            } catch {}
                            fusedWriter = null;
                            fusedReadSeq = GlobalSeq; // Drop to head
                            OpenFusedFile();
                            try {
                                fusedWriter.WriteLine($"{{\"tag\":\"CONTROL\",\"type\":\"FUSED_RESTART\",\"reason\":\"stall_{deltaMs}ms\",\"ts\":\"{NextGlobalUtc():O}\"}}");
                            } catch {}
                        }
                    }
                    lastLoopTicks = now;

                    // Patch 10: Ring Watermark (Emit every 5 seconds)
                    if (now - lastWatermarkTicks > Stopwatch.Frequency * 5)
                    {
                        lastWatermarkTicks = now;
                        long peak = Interlocked.Exchange(ref RingHighWater, 0);
                        double pct = (double)peak / RING_SIZE * 100.0;
                        try {
                            if (fusedWriter != null)
                                fusedWriter.WriteLine($"{{\"tag\":\"RING_WATERMARK\",\"ts\":\"{NextGlobalUtc():O}\",\"backlog\":{peak},\"pct\":{pct:F2}}}");
                        } catch {}
                    }

                    var idx = fusedReadSeq & MASK;
                    var entry = ring[idx];

                    // Blocking Kernel (No Inference)
                    if (entry.Seq != fusedReadSeq)
                    {
                        spin.SpinOnce();

                        // Maintenance checks while spinning
                        if (now - lastDiskFlushTicks > flushIntervalTicks)
                        {
                            if (fusedWriter != null) {
                                fusedWriter.Flush();
                                // [PHASE 2] ROTATION
                                if (fusedWriter.BaseStream.Length > 10L * 1024 * 1024 * 1024)
                                {
                                    fusedWriter.Close();
                                    fusedWriter = null;
                                    FusedPart++;
                                    OpenFusedFile();
                                }
                            }
                            lastDiskFlushTicks = now;
                            OpenFusedFile();
                        }

                        continue;
                    }

                    // Write
                    if (fusedWriter == null) OpenFusedFile();
                    fusedWriter.Write(entry.Json);
                    fusedRowsWritten++;

                    // Ring Cleanup
                    ring[idx] = default(FusedEntry);

                    fusedReadSeq++;
                    fusedLastWriteTicks = now;

                    // Maintenance checks (post-write)
                    if (now - lastDiskFlushTicks > flushIntervalTicks)
                    {
                        if (fusedWriter != null) {
                            fusedWriter.Flush();
                            // [PHASE 2] ROTATION
                            if (fusedWriter.BaseStream.Length > 10L * 1024 * 1024 * 1024)
                            {
                                fusedWriter.Close();
                                fusedWriter = null;
                                FusedPart++;
                                OpenFusedFile();
                            }
                        }
                        lastDiskFlushTicks = now;
                        OpenFusedFile();
                    }
                    fusedLastHeartbeatTicks = now;
                    spin.Reset();
                }
                catch (Exception ex)
                {
                    Thread.Sleep(50);
                }
            }

            if (fusedWriter != null) try { fusedWriter.Flush(); } catch {}
            Interlocked.Exchange(ref fusedWriterRunning, 0);
        }

        private void HeartbeatLoop()
        {
            DateTime lastHealthUtc = DateTime.MinValue;

            while (!heartbeatCts.Token.IsCancellationRequested)
            {
                DateTime nowUtc = NextGlobalUtc();

                // -----------------------------------------------------
                // PHASE 0: SYSTEM TELEMETRY (Every 5s)
                // -----------------------------------------------------
                if ((nowUtc - lastHealthUtc).TotalSeconds >= 5)
                {
                    lastHealthUtc = nowUtc;
                    long lag = GlobalSeq - fusedReadSeq;
                    long hwm = Interlocked.Exchange(ref RingHighWater, 0); // Reset HWM
                    long idle = (long)((Stopwatch.GetTimestamp() - fusedLastWriteTicks) * 1000.0 / Stopwatch.Frequency);
                    long totalDrops = 0;
                    long p95Max = 0;

                    if (dom != null)
                    {
                        foreach (var d in dom)
                        {
                            if (d == null || !d.IsMonitoring) continue;
                            totalDrops += d.StructuralDrops1s;
                            d.StructuralDrops1s = 0; // Reset
                            if (d.LatencyP95 > p95Max) p95Max = (long)d.LatencyP95;
                        }
                    }

                    // PHASE 2: DISK GUARD
                    try {
                         // Only check if writing
                         if (WriteFused && fusedWriter != null) {
                             string drive = Path.GetPathRoot(NinjaTrader.Core.Globals.UserDataDir);
                             DriveInfo di = new DriveInfo(drive);
                             if (di.AvailableFreeSpace < 5L * 1024 * 1024 * 1024) { // 5GB
                                  EmitControlEvent("SYSTEM", "DISK_LOW_STOP");
                                  WriteFused = false; // Emergency Stop
                             }
                         }
                    } catch {}

                    EmitControlEvent("SYSTEM_HEALTH",
                        $"lag={lag},hwm={hwm},fused_skips={Interlocked.Read(ref fusedGapSkips)},drops_1s={totalDrops},p95={p95Max},idle_ms={idle}");
                }

                if (dom != null)
                {
                    foreach (var d in dom)
                    {
                        if (d == null || !d.IsMonitoring) continue;
                        if ((nowUtc - d.LastHeartbeatUtc).TotalSeconds >= HeartbeatSec && HeartbeatSec > 0)
                        {
                            d.LastHeartbeatUtc = nowUtc;
                            var hbPayload = new LightDomPayload
                            {
                                Tag = Tags.HEARTBEAT,
                                HostUtc = nowUtc
                            };
                            d.SnapshotQueue.Enqueue(hbPayload);
                        }
                    }
                }
                Thread.Sleep(1000);
            }
        }

        // ==========================================================
        // EMIT ROW
        // ==========================================================
        private void EmitRow(DomState d, DateTime eventTime, byte tag, long tradeVol, double eventPrice, byte liquidityEvent, byte tradeCausalityLabel = CausalLabels.NONE, bool force = false, byte forceReason = Reasons.NONE, int liqSide = 0, int liqLevel = -1, long liqDelta = 0, bool? trainEligibleOverride = null, long sumBidK = 0, long sumAskK = 0, long currentOfi = 0, long currentDepthOfi = 0)
        {
            // SINGLE SOURCE OF TRUTH
            DateTime nowUtc = d.NextHostUtc();

            // --------------------------------------------------------------------------------
            // HIGH WATER MARK GUARD
            // Prevents OOM crashes if writer lags significantly (e.g. during Non-Farm Payrolls)
            // --------------------------------------------------------------------------------
            const int HWM = 50000;
            const int LWM = 30000;

            // Check Ring Buffer lag
            long lag = GlobalSeq - fusedReadSeq;
            // Patch 10: Atomic High Watermark Tracking
            long prev;
            do
            {
                prev = RingHighWater;
                if (lag <= prev) break;
            }
            while (Interlocked.CompareExchange(ref RingHighWater, lag, prev) != prev);

            // Session Max Lag
            long prevMax;
            do { prevMax = MaxRingLag; if (lag <= prevMax) break; } while (Interlocked.CompareExchange(ref MaxRingLag, lag, prevMax) != prevMax);

            bool fusedLagging = WriteFused && (lag > RING_SIZE - 1000);

            if (d.SnapshotQueue.Count > HWM || fusedLagging) d.BackpressureTripped = true;
            else if (d.BackpressureTripped && d.SnapshotQueue.Count < LWM && (!WriteFused || lag < RING_SIZE / 2)) d.BackpressureTripped = false;

            if (d.BackpressureTripped)
            {
                d.DropsHWM++;
                d.RowsDropped++;
                return;
            }

            d.RowsAttempted++;

            // Track per-instrument liveliness and stats
            if (d.Symbol.StartsWith("ES")) { esLive = true; esRows++; }
            if (d.Symbol.StartsWith("NQ")) { nqLive = true; nqRows++; }

            // [FIX] SPOOF / ICEBERG / FEATURE STATE MOVED TO OnMarketDepth
            // (Removed duplicate calls)

            // [NEW] Calculate Delta Exch Ms
            // Do this BEFORE updating d.LastExchTs
            double deltaExchMs = 0;
            if (d.LastExchTs > DateTime.MinValue)
            {
                deltaExchMs = (eventTime - d.LastExchTs).TotalMilliseconds;
                if (deltaExchMs < 0) deltaExchMs = 0; // Prevent negative on regression
            }

            bool exchRegress = false;
            DateTime rawExchTs = eventTime;

            if (eventTime < d.LastExchTs)
            {
                eventTime = d.LastExchTs;
                d.ExchTsBackfillCount++;
                d.ExchRegressCount++;
                exchRegress = true;
            }
            d.LastExchTs = eventTime;

            if (tag == Tags.DOM && !d.IsDomBootstrapped)
            {
                // Allow bootstrap rows through unconditionally
                force = true;

                if (d.ValidBookCount >= PressureLevels)
                    d.IsDomBootstrapped = true;
            }

            if (tag == Tags.TRADE && !d.IsDomBootstrapped)
            {
                d.DropsStructural++;
                d.RowsDropped++;
                return;
            }


            if (tag == Tags.DOM && d.IsDomBootstrapped && MinSnapshotIntervalMs > 0 && !force)
            {
                if (d.LastSnapshotUtc != DateTime.MinValue &&
                    (nowUtc - d.LastSnapshotUtc).TotalMilliseconds < MinSnapshotIntervalMs)
                {
                    d.DropsPolicyInterval++;
                    d.RowsDropped++;
                    return;
                }
                d.LastSnapshotUtc = nowUtc;
            }

            long dtMs = (d.LastEventUtc == DateTime.MinValue) ? 0 : (long)Math.Max(0, (nowUtc - d.LastEventUtc).TotalMilliseconds);

            if (d.LastEmittedExchTs > DateTime.MinValue && eventTime < d.LastEmittedExchTs)
            {
                d.DropsStructural++;
                return;
            }

            long latency = (long)(nowUtc.Subtract(eventTime.ToUniversalTime()).TotalMilliseconds);

            double bid = d.LastBidPx;
            double ask = d.LastAskPx;

            // --- Realized Volatility Update ---
            // --- Realized Volatility Update ---
            if (d.RvLastReset1s == DateTime.MinValue)
            {
                d.RvLastReset1s = d.RvLastReset5s = d.RvLastReset30s = nowUtc;
            }

            double mid = (bid > 0 && ask > 0) ? 0.5 * (bid + ask) : d.LastMidPx;

            double exchTsMs = (eventTime - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            var ll = A5UnifiedLeadLagEngineV309.Update(d.Symbol, mid, (long)exchTsMs, _instanceId);

            if (d.LastMidPx > 0 && mid > 0)
            {
                double ret = mid - d.LastMidPx;
                double ret2 = ret * ret;

                DateTime now = nowUtc;
                double dt = (now - d.LastMidTs).TotalSeconds;
                if (dt < 0) dt = 0;

                // [PATCH 8] EMA RV (1s Window)
                // Variance EMA: Var_t = alpha * ret^2 + (1-alpha) * Var_{t-1}
                double decay = Math.Exp(-dt / 1.0);

                if (!d.InBurst)
                {
                    d.RvVarEma = decay * d.RvVarEma + (1.0 - decay) * ret2;

                    double decay5 = Math.Exp(-dt / 5.0);
                    d.RvVarEma5s = decay5 * d.RvVarEma5s + (1.0 - decay5) * ret2;

                    double decay30 = Math.Exp(-dt / 30.0);
                    d.RvVarEma30s = decay30 * d.RvVarEma30s + (1.0 - decay30) * ret2;
                }

                // Decay other EMAs (Continuous Time)
                d.TradeImbEma      *= decay;
                d.FlipIntensityEma *= decay;

                // Spread Vel tracks state
                d.SpreadVelEma = decay * d.SpreadVelEma + (1.0 - decay) * d.SpreadVel;

                // Pressure decays toward current state
                if (!d.InBurst)
                {
                    d.PressureEma = decay * d.PressureEma + (1.0 - decay) * d.LastActivePressure;
                }

                // =============================
                // ML Feature: Mid Price Kinematics
                // =============================
                if (d.LastMidTs != DateTime.MinValue && !d.InBurst)
                {
                    double midDtMs = (nowUtc - d.LastMidTs).TotalMilliseconds;
                    // [PATCH 4] Clamp dt floor (WAN Safe)
                    midDtMs = Math.Max(midDtMs, 5.0);

                    if (midDtMs > 0 && midDtMs <= 500)
                    {
                        double vel = (mid - d.LastMidPx) / midDtMs;
                        d.MidAccel = (vel - d.MidVel) / midDtMs;
                        d.MidVel = vel;
                        double tick = TickSize > 0 ? TickSize : 1.0;
                        d.MidVelTicks   = d.MidVel   / tick;
                        d.MidAccelTicks = d.MidAccel / tick;

                        // [PATCH 3] Raw Physics - NO TANH
                        // Squashing belongs in Python.
                        // d.MidVel etc are kept RAW for correct integration.
                    }
                }
                d.LastMidTs = nowUtc;
            }
            d.LastMidPx = mid;

            double rawBid = bid;
            double rawAsk = ask;
            bool rawCrossed = (rawBid > 0 && rawAsk > 0 && rawAsk <= rawBid);
            bool rawLocked = (rawBid > 0 && rawAsk > 0 && rawAsk == rawBid);

            byte repairType = RepairTypes.NONE;
            byte repairReason = RepairReasons.NONE;
            byte repairMethod = RepairMethods.NONE;
            byte repairRule = RepairRules.NONE;
            double repairConf = 1.0;

            if (forceReason != Reasons.NONE)
            {
                if (forceReason == Reasons.INSERT_EVENT)
                    repairType = RepairTypes.SOFT;
                else
                    repairType = RepairTypes.STRUCTURAL;

                if(forceReason == Reasons.GAP_FILL) repairReason = RepairReasons.GAP_FILL;
                else if(forceReason == Reasons.INSERT_EVENT) repairReason = RepairReasons.INSERT_EVENT;
            }

            double bookAge = (eventTime - d.LastDepthUpdateExchTs).TotalMilliseconds;
            Nullable<DomSnapshot> causalSnap = null;
            if (tag == Tags.TRADE)
            {
                lock (d.HistoryLock)
                {
                    int scanned = 0;
                    if (d.HistoryCount > 0)
                    {
                        int currentIdx = (d.HistoryHead - 1 + HISTORY_BUFFER_SIZE) % HISTORY_BUFFER_SIZE;
                        for(int i=0; i<d.HistoryCount && scanned < 50; i++)
                        {
                            var snap = d.HistoryBuffer[currentIdx];
                            if (snap.ExchTs <= eventTime)
                            {
                                causalSnap = snap;
                                break;
                            }
                            currentIdx = (currentIdx - 1 + HISTORY_BUFFER_SIZE) % HISTORY_BUFFER_SIZE;
                            scanned++;
                        }
                    }
                }
                if (causalSnap.HasValue)
                {
                    bid = causalSnap.Value.BidPx;
                    ask = causalSnap.Value.AskPx;
                    bookAge = (eventTime - causalSnap.Value.ExchTs).TotalMilliseconds;
                }
            }

            int xb = 0;
            if (bid > 0 && ask > 0)
            {
                if (ask > bid)
                {
                    if (d.ValidBookCount < 1000000) d.ValidBookCount++;
                    d.LastGoodBidPx = bid;
                    d.LastGoodAskPx = ask;

                    if (Math.Abs(bookAge) > 15)
                    {
                        repairType = RepairTypes.ANNOTATION;
                        repairReason = RepairReasons.DOM_AGE_UPDATE;
                        repairRule = RepairRules.TIME_SKEW_CHK;
                        repairConf = 0.90;
                    }
                }
                else
                {
                    // ML strict: discard crossed books entirely
                    if (rawBid >= rawAsk)
                    {
                        d.DropsStructural++;
                        return;
                    }

                    d.RejectedCrossed++;
                    d.DropsStructural++;
                    return;
                }
            }

            double spread = (bid > 0 && ask > 0) ? (ask - bid) : 0.0;
            int spreadTicks = (TickSize > 0) ? (int)Math.Round(spread / TickSize, MidpointRounding.AwayFromZero) : 0;

            bool priceMatch = true;
            int ticksDiff = 0;
            bool withinSpread = false;
            bool causalPass = tag != Tags.TRADE;
            if (tag == Tags.TRADE && bid > 0 && ask > 0)
            {
                double tol = 2 * TickSize;
                priceMatch = (eventPrice >= bid - tol) && (eventPrice <= ask + tol);

                if (causalSnap.HasValue)
                    withinSpread = (eventPrice > causalSnap.Value.BidPx && eventPrice < causalSnap.Value.AskPx);
                else
                    withinSpread = (eventPrice > bid && eventPrice < ask);

                if (priceMatch && ticksDiff == 0) withinSpread = true;

                double midPx = (bid + ask) * 0.5;
                if (TickSize > 0) ticksDiff = (int)Math.Round(Math.Abs(eventPrice - midPx) / TickSize);

                causalPass = priceMatch && withinSpread;
                if (!causalSnap.HasValue) causalPass = false;
            }

            double alphaDecay = Math.Exp(-0.001 * Math.Abs(dtMs));
            double bookFresh = Math.Exp(-0.005 * Math.Abs(bookAge));
            double spreadW = (spreadTicks > 0) ? (1.0 / spreadTicks) : 1.0;
            double causalScore = alphaDecay * bookFresh * spreadW;

            byte causalityLabel = (bookAge >= 0) ? CausalLabels.PRE_TRADE : CausalLabels.POST_TRADE;
            if(tag == Tags.TRADE) {
                if(tradeCausalityLabel != CausalLabels.NONE) causalityLabel = tradeCausalityLabel;
                else if(causalSnap.HasValue) causalityLabel = CausalLabels.PRE_TRADE;
                else if(d.HistoryCount == 0) causalityLabel = CausalLabels.NO_DOM_CONTEXT;
                else causalityLabel = CausalLabels.TIMEOUT;
            }

            long anchorDomSeq = -1;
            byte anchorType = AnchorTypes.NONE;
            double exchTsDeltaMs = 0.0;
            double hostTsDeltaMs = 0.0;
            double anchorConf = 0.0;
            byte anchorReason = AnchorReasons.NONE;

            if (tag == Tags.TRADE)
            {
                if (causalSnap.HasValue)
                {
                    anchorDomSeq = causalSnap.Value.Seq;
                    exchTsDeltaMs = (eventTime - causalSnap.Value.ExchTs).TotalMilliseconds;
                    // [PATCH 1] Use monotonic nowUtc instead of arrivalTime
                    hostTsDeltaMs = (nowUtc - causalSnap.Value.HostUtc).TotalMilliseconds;

                    if (priceMatch && withinSpread && Math.Abs(bookAge) < 2)
                    {
                        anchorType = AnchorTypes.STRICT_MATCH;
                        anchorConf = 0.95;
                    }
                    else
                    {
                        anchorType = AnchorTypes.BEST_PRIOR;
                        anchorConf = 0.5 * causalScore;
                    }

                    if (d.BurstTradeCount > 1)
                    {
                        anchorReason = AnchorReasons.EXCH_BATCH_AMBIGUITY;
                        if (anchorType == AnchorTypes.STRICT_MATCH) anchorConf *= 0.8;
                    }
                }
                else
                {
                    if (d.LastEmittedDomSeq >= 0)
                        anchorDomSeq = d.LastEmittedDomSeq;

                    anchorType = AnchorTypes.NONE;
                    anchorReason = AnchorReasons.NO_PRIOR_HISTORY;
                }
            }

            int blockedMask = 0;
            if (latency > 500) blockedMask |= 1;
            if (dtMs < 0) blockedMask |= 2;
            if (Math.Abs(bookAge) > 100) blockedMask |= 4;
            if (spreadTicks > 2) blockedMask |= 8;
            bool execFeasible = (blockedMask == 0);

            bool isStructural = repairType == RepairTypes.STRUCTURAL;
            bool usableForTraining = !isStructural && causalPass && latency <= 3000;
            if (trainEligibleOverride.HasValue) usableForTraining = trainEligibleOverride.Value;

            if (anchorType == AnchorTypes.BEST_PRIOR && anchorConf < 0.6)
                usableForTraining = false;

            byte trainBlockReason = PolReasons.NONE;
            if (isStructural) trainBlockReason = PolReasons.STRUCTURAL_REPAIR;
            else if (!causalPass) trainBlockReason = PolReasons.CAUSAL_FAIL;
            else if (latency > 3000) trainBlockReason = PolReasons.LATENCY_GT_3S;
            else if (!usableForTraining && !execFeasible) trainBlockReason = PolReasons.POLICY_FAIL;

            bool isRth = sessionIterators[d.BarsIndex].IsInSession(eventTime, true, true);
            bool isWarmup = false;
            if (isRth && eventTime.TimeOfDay < sessionIterators[d.BarsIndex].ActualSessionBegin.TimeOfDay.Add(TimeSpan.FromMinutes(5)))
                    isWarmup = true;
            if (isWarmup) usableForTraining = false;

            // Policy
            // ALWAYS WRITE — sellable is metadata only
            bool polTrainExec = !isStructural && causalPass && latency <= 200;
            bool polExecLive = execFeasible && causalScore > 0.35;

            // [PATCH 5] Structural Hard Stop
            if (isStructural)
            {
                polTrainExec = false;
                polExecLive = false;
            }

            if (usableForTraining)
            {
                if (tag == Tags.TRADE) d.TrainingCountTrade++; else d.TrainingCountDOM++;
            }
            if (polTrainExec)
            {
                if (tag == Tags.TRADE) d.ExecCountTrade++; else d.ExecCountDOM++;
            }

            double stableMs = (nowUtc - d.LastTopChangeUtc).TotalMilliseconds;
            bool clockMonotonic = true;
            if (stableMs < d.LastStableMs) clockMonotonic = false;
            d.LastStableMs = stableMs;
            if (!clockMonotonic && stableMs > 1000) {
                d.RowsDropped++;
                return;
            }

            byte burstType = BurstTypes.NONE;
            double sweepTicks = 0;

            if (tag == Tags.TRADE)
            {
                if ((eventTime - d.LastBurstTime).TotalMilliseconds <= 5)
                {
                    d.BurstTradeCount++;
                    if (d.BurstTradeCount > 4) d.BurstTradeCount = 4;
                    burstType = BurstTypes.SAME_EXCH_TS;

                    d.BurstMaxPx = Math.Max(d.BurstMaxPx, eventPrice);
                    d.BurstMinPx = Math.Min(d.BurstMinPx, eventPrice);
                }
                else
                {
                    d.BurstId++;
                    d.BurstTradeCount = 1;
                    burstType = BurstTypes.PRICE_CLUSTER;

                    d.BurstMaxPx = eventPrice;
                    d.BurstMinPx = eventPrice;
                }

                d.LastBurstTime = eventTime;

                if (TickSize > 0)
                    sweepTicks = (d.BurstMaxPx - d.BurstMinPx) / TickSize;
            }

            byte bookAgeClass = BookAges.STALE;
            if (Math.Abs(bookAge) <= 5) bookAgeClass = BookAges.FRESH;
            else if (Math.Abs(bookAge) <= 15) bookAgeClass = BookAges.WARM;

            byte aggr = Aggressors.NA;
            if (tag == Tags.TRADE)
            {
                if (d.Pending.Active && d.Pending.Vol == tradeVol && Math.Abs(d.Pending.Utc.Subtract(nowUtc).TotalMilliseconds) < 2000)
                {
                    if (d.Pending.Side > 0) aggr = Aggressors.B;
                    else if (d.Pending.Side < 0) aggr = Aggressors.S;
                    else aggr = Aggressors.M;
                }
                else aggr = Aggressors.U;
            }

            int aggrTicks = 0;
            if (tag == Tags.TRADE && TickSize > 0)
            {
                // If buying, how far above the Best Ask did we pay?
                // If selling, how far below the Best Bid did we sell?
                // Note: Use d.LastAskPx / d.LastBidPx (Pre-Trade prices)

                if (eventPrice > d.LastAskPx && d.LastAskPx > 0)
                    aggrTicks = (int)Math.Round((eventPrice - d.LastAskPx) / TickSize);

                else if (eventPrice < d.LastBidPx && d.LastBidPx > 0)
                    aggrTicks = (int)Math.Round((d.LastBidPx - eventPrice) / TickSize);

                // Ensure non-negative (0 means they hit the touch exactly)
                if (aggrTicks < 0) aggrTicks = 0;
            }

            // =============================
            // ML Feature: Signed Trade Imbalance (EMA)
            // =============================
            // Decay handled in EmitRow

            if (aggr == Aggressors.B) d.TradeImbEma += tradeVol;
            else if (aggr == Aggressors.S) d.TradeImbEma -= tradeVol;

            // Legacy map
            d.TradeImb1s = (long)d.TradeImbEma;

            // --- Feature 2: Time-Since (Calc before update) ---
            long msSinceTrade = d.LastTradeTs == DateTime.MinValue ? -1 : (long)(nowUtc - d.LastTradeTs).TotalMilliseconds;
            long msSinceTop = d.LastTopChangeTs == DateTime.MinValue ? -1 : (long)(nowUtc - d.LastTopChangeTs).TotalMilliseconds;
            long msSinceSpread = d.LastSpreadChangeTs == DateTime.MinValue ? -1 : (long)(nowUtc - d.LastSpreadChangeTs).TotalMilliseconds;

            // --- Feature 3: Slope (Calc & Update) ---
            double pressureSlope = 0;
            double qimbSlope = 0;
            double dtSlope = (nowUtc - d.LastSlopeTs).TotalMilliseconds;
            if (dtSlope >= 50 && dtSlope <= 500)
            {
                pressureSlope = (d.LastActivePressure - d.PrevActivePressure) / dtSlope;
                qimbSlope     = (d.LastActiveQimb     - d.PrevActiveQimb)     / dtSlope;

                d.LastSlopeTs  = nowUtc;
            }

            // --- Feature 4: Event Density (Update) ---
            if ((nowUtc - d.DensityResetTs).TotalMilliseconds >= 100)
            {
                d.DomCnt100ms = d.TradeCnt100ms = d.CancelCnt100ms = 0;
                d.DensityResetTs = nowUtc;
            }
            if (tag == Tags.DOM) d.DomCnt100ms++;
            if (tag == Tags.TRADE) d.TradeCnt100ms++;
            if (liquidityEvent == LiqEvents.CANCEL) d.CancelCnt100ms++;

            if (tag != Tags.TRADE)
            {
                int validBids = 0;
                for (int i = 0; i < DomState.DEPTH; i++) if (!double.IsNaN(d.BidPx[i]) && d.BidPx[i] <= bid + (TickSize * 0.00001)) validBids++;
                int validAsks = 0;
                for (int i = 0; i < DomState.DEPTH; i++) if (!double.IsNaN(d.AskPx[i]) && d.AskPx[i] >= ask - (TickSize * 0.00001)) validAsks++;

                if (!d.IsDomBootstrapped) {}
                else if (validBids == 0 || validAsks == 0) {
                    d.RowsDropped++; return;
                }
            }

            byte domReason = Reasons.TIMER;
            byte state = States.ACTIVE;
            bool changed = false;
            if (tag != Tags.TRADE)
            {
                bool priceChanged = Math.Abs(d.CurrentBidPx - d.LastWriteBidPx) > 1e-9 || Math.Abs(d.CurrentAskPx - d.LastWriteAskPx) > 1e-9;
                bool sizeChanged = d.CurrentTouchBid != d.LastWriteTouchBid || d.CurrentTouchAsk != d.LastWriteTouchAsk;
                bool deepChanged = d.CurrentSumBid != d.LastWriteSumBid || d.CurrentSumAsk != d.LastWriteSumAsk;
                bool flagActive = d.PendingSpoofFlag != 0 || d.PendingIcebergFlag != 0;

                changed = priceChanged || sizeChanged || deepChanged || flagActive;
                bool heartbeat = (nowUtc - d.LastWriteUtc).TotalMilliseconds > 1000;

                if (!d.IsDomBootstrapped && tag == Tags.DOM && d.ValidBookCount >= PressureLevels)
                {
                    domReason = Reasons.BOOTSTRAP;
                    state = States.ACTIVE;
                    force = true;
                }

                if (!changed && !heartbeat && !force)
                {
                    d.RowsDropped++;
                    return;
                }

                if (flagActive) domReason = Reasons.RISK;
                else if (priceChanged)
                {
                    domReason = Reasons.QUOTE;
                    d.LastTopChangeUtc = nowUtc;
                    d.LastTopChangeTs = nowUtc; // Feature 2 Update
                    // [PATCH 8] Flip Intensity EMA
                    // Decay handled in EmitRow. Just add impulse.
                    d.FlipIntensityEma += 1.0;

                    d.TopFlipCount1s = (int)d.FlipIntensityEma;
                }
                else if (sizeChanged) domReason = Reasons.VOL;
                else
                {
                    if (deepChanged) domReason = Reasons.VOL;
                    else domReason = Reasons.TIMER;
                }
            }
            else
            {
                domReason = Reasons.TRADE;
                state = States.IMPACT;
            }

            if(forceReason != Reasons.NONE) domReason = forceReason;

            if (tag != Tags.TRADE)
            {
                if ((nowUtc - d.LastTradeUtc).TotalMilliseconds <= 50)
                    state = States.IMPACT;
                else if (domReason == Reasons.TIMER)
                    state = States.IDLE;
                else
                    state = States.ACTIVE;
            }

            if (domReason == Reasons.BOOTSTRAP)
            {
                NinjaTrader.Code.Output.Process($"[A5] DOM BOOTSTRAP EMITTED {d.Symbol} seq={d.Seq}", PrintTo.OutputTab1);
            }

            // Feature 2: Spread Change Update
            if (tag == Tags.DOM && spreadTicks != d.SpreadTicks)
            {
                double dt = (nowUtc - d.LastSpreadChangeTs).TotalMilliseconds;
                // [PATCH 4] Clamp dt floor (WAN Safe)
                dt = Math.Max(dt, 5.0);

                if (dt > 0 && dt <= 500)
                    d.SpreadVel = (spreadTicks - d.LastSpreadTicks) / dt;

                d.LastSpreadTicks = spreadTicks;
                d.LastSpreadChangeTs = nowUtc;
                d.SpreadTicks = spreadTicks;
            }

            if (tag != Tags.TRADE && domReason == Reasons.TIMER && !changed && !force)
            {
                d.DropsPolicyInterval++;
                d.RowsDropped++;
                return;
            }

            // [PATCH 1] Update LastWriteUtc on successful emit
            if (tag != Tags.TRADE)
            {
                d.LastWriteUtc = nowUtc;
            }

            // Feature 2: Trade Update
            if (tag == Tags.TRADE)
            {
                // [PATCH 9] Session VWAP
                d.SessionVol += tradeVol;
                d.SessionPV += eventPrice * tradeVol;
                if (d.SessionVol > 0) d.SessionVWAP = d.SessionPV / d.SessionVol;

                d.LastTradeTs = nowUtc;
                d.LastTradeUtc = nowUtc; // Sync for Impact detection
            }

            // Update Latency Stats (sampled from all non-dropped rows)
            // [PATCH 5] Extended Histogram for WAN tail
            int latIdx = (int)(Math.Min(2000, latency));
            if (latIdx < 0) latIdx=0;
            d.LatencyHistogram[latIdx]++;
            d.LatencyCount++;
            if (latency > d.LatencyMax) d.LatencyMax = latency;


            double ofiStd = (d.OfiCnt > 5) ? Math.Sqrt(d.OfiVar / (d.OfiCnt - 1)) : 0;
            int ofiShock = (ofiStd > 0 && Math.Abs(d.CurrentOfi) > 3.0 * ofiStd) ? 1 : 0;

            long msInSpread = d.LastSpreadChangeTs == DateTime.MinValue ? -1 : (long)(nowUtc - d.LastSpreadChangeTs).TotalMilliseconds;

            CausalityInfo causality = new CausalityInfo
            {
                WindowMs = (int)Math.Abs(dtMs),
                Locked = true,
                Score = causalScore,
                Label = (causalityLabel == CausalLabels.PRE_TRADE) ? "pre_trade" :
                        (causalityLabel == CausalLabels.POST_TRADE) ? "post_trade" : "none"
            };

            // ===== ML FEATURES =====

            // L1 Imbalance
            double imbL1 = 0;
            double denom = d.LastBidSize + d.LastAskSize;
            if (denom > 0)
                imbL1 = (d.LastBidSize - d.LastAskSize) / denom;

            // Spread in basis points
            double spreadBps = 0;
            if (mid > 0)
                spreadBps = (spread / mid) * 10000.0;

            var p = new LightDomPayload
            {
                Seq = ++d.Seq,
                // ----------------------------------------------------------------------
                // PATCH 1: ATOMIC ALLOCATION
                // Moved to Writer Loop for tighter alignment
                // ----------------------------------------------------------------------
                GlobalSeq = 0,

                RawExchTs = rawExchTs,
                ExchRegress = exchRegress ? 1 : 0,

                ExchTs = eventTime,
                HostUtc = nowUtc,
                InstSeq = d.Seq,
                IsTrade = (tag == Tags.TRADE),
                Tag = tag,
                Reason = domReason,
                State = state,
                LiquidityEvent = liquidityEvent,
                Aggr = aggr,
                Crossed = rawCrossed,
                Locked = rawLocked,
                RawBid = rawBid,
                RawAsk = rawAsk,
                // 🔑 L1 queue sizes (CRITICAL FOR AI)
                L1Bid = d.LastBidSize,
                L1Ask = d.LastAskSize,

                BookAgeMs = bookAge,
                BookAgeClass = bookAgeClass,
                DomSeqRef = (causalSnap.HasValue) ? causalSnap.Value.Seq : (d.LastEmittedDomSeq >= 0 ? d.LastEmittedDomSeq : -1),
                PxExact = priceMatch,
                PxTicksDiff = ticksDiff,
                PxWithinSpread = withinSpread,
                CausalPass = causalPass,
                RepairType = repairType,
                RepairReason = repairReason,
                RepairMethod = repairMethod,
                RepairRuleId = repairRule,
                RepairConf = repairConf,
                ExecFeasible = execFeasible,
                ExecLatency = latency,
                ExecBlockedMask = blockedMask,
                CausalityLabel = causalityLabel,
                CausalityWindow = causality.WindowMs,
                CausalityLocked = causality.Locked,
                CausalityScore = causality.Score,
                PolWarmup = warmup,
                PolRth = isRth,
                PolTrainRes = usableForTraining,
                PolTrainExec = polTrainExec,
                PolExecLive = polExecLive,
                PolReason = trainBlockReason,
                AnchorDomSeq = anchorDomSeq,
                AnchorType = anchorType,
                AnchorExchDelta = exchTsDeltaMs,
                AnchorHostDelta = hostTsDeltaMs,
                AnchorConf = anchorConf,
                AnchorReason = anchorReason,
                HasDomContext = (causalSnap.HasValue),
                TrainEligible = usableForTraining,
                TrainBlockReason = trainBlockReason,
                StableMs = stableMs,
                BurstId = d.BurstId,
                BurstCnt = d.BurstTradeCount,
                BurstType = burstType,
                ClockMonotonic = clockMonotonic,
                DtMs = dtMs,
                Latency = latency,
                LastPx = (tag == Tags.TRADE) ? eventPrice : d.LastKnownTradePx,
                Bid = bid,
                Ask = ask,
                Spread = spread,
                SpreadTicks = spreadTicks,
                ImbL1 = imbL1,
                SweepTicks = sweepTicks,
                SpreadBps = spreadBps,

                Sig = (int)ll.sig,
                Llr = ll.llr,
                LeadLagMs = ll.lag,
                LeadLagCorr = ll.corr,
                NqZ = ll.nqZ,
                EsZ = ll.esZ,

                // [NEW] Populate fields
                DeltaExchMs = deltaExchMs,
                AggrTicks   = (tag == Tags.TRADE) ? aggrTicks : 0,
                LiqSide     = liqSide,
                LiqLevel    = liqLevel,
                LiqDelta    = liqDelta
            };

            // [PATCH 6] Causal Exclusion Logic for LeadLag
            if (p.Sig != 0 || Math.Abs(p.Llr) > 0.01)
            {
                p.TrainEligible = false;
                p.TrainBlockReason = PolReasons.CAUSAL_FAIL;
            }

            // [PATCH 5] Market Regime
            p.MarketRegime = (p.Crossed || !p.CausalPass || Math.Abs(p.PxTicksDiff) > 20)
                ? "DISLOCATED"
                : "NORMAL";

            // [PATCH 6] Latency Admission Mask
            // Need p95Lat from d.LatencyP95 (updated in WriteLatencyStats or similar)
            // If p95 is 0 (start), we might be lenient or strict. Assume lenient until stats build.
            double p95Limit = d.LatencyP95 > 0 ? d.LatencyP95 : 1000.0;

            if (p.TrainEligible)
            {
                // Refine TrainEligible
                bool latPass = p.Latency < p95Limit;
                bool regressPass = p.ExchRegress == 0; // ExchRegress is int (0 or 1)

                // Logic: p.CausalPass && !p.ExchRegress && !p.Crossed && p.LatencyMs < p95Lat;
                p.TrainEligible = p.CausalPass && regressPass && !p.Crossed && latPass;
            }

            // ================================
            // WIDE SPREAD FILTER (NQ)
            // ================================
            if (d.Symbol.StartsWith("NQ") && spreadTicks >= 3)
            {
                p.TrainEligible = false;
                p.TrainBlockReason = PolReasons.WIDE_SPREAD;
            }

            // OPTIONAL BUT HIGHLY RECOMMENDED
            if (d.InBurst)
            {
                p.TrainEligible = false;
                p.TrainBlockReason = PolReasons.EXCHANGE_BURST;
            }

            // [PATCH 7] OFI Z-Score
            // Calculated using state from EmitRow start
            double ofiZ = 0;

            // Require statistical mass before normalization
            if (d.OfiCnt >= 50)
            {
                ofiZ = (ofiStd > 1e-6) ? (d.CurrentOfi - d.OfiMean) / ofiStd : 0;

                // Clamp extreme noise
                if (ofiZ > 2) ofiZ = 2;
                else if (ofiZ < -2) ofiZ = -2;
            }

            if (isStructural)
            {
                ofiZ = 0;
            }

            p.OfiZ = ofiZ;

            // [PATCH 8] Structural Density
            // Reset if needed
            if ((nowUtc - d.StructuralDropsResetTs).TotalSeconds >= 1.0)
            {
                d.StructuralDrops1s = 0;
                d.StructuralDropsResetTs = nowUtc;
            }
            p.StructuralRate1s = d.StructuralDrops1s;

            p.Rv1s  = Math.Sqrt(d.RvVarEma);
            p.Rv5s  = Math.Sqrt(d.RvVarEma5s);
            p.Rv30s = Math.Sqrt(d.RvVarEma30s);
            p.QueuePosBid = d.QueuePosBid;
            p.QueuePosAsk = d.QueuePosAsk;
            p.MsInSpread = msInSpread;
            p.OfiShock = ofiShock;
            p.MidVelTicks   = d.MidVelTicks;
            p.MidAccelTicks = d.MidAccelTicks;
            p.TopFlipCount1s = d.TopFlipCount1s;
            p.SpreadVel = d.SpreadVel;
            p.OfiSkewEma = d.OfiSkewEma;
            p.CancelAddRatio1s = d.CancelAddRatio1s;

            // Feature 2
            p.MsSinceTrade = msSinceTrade;
            p.MsSinceTop = msSinceTop;
            p.MsSinceSpread = msSinceSpread;

            // Feature 3
            p.PressureSlope = pressureSlope;
            p.QimbSlope = qimbSlope;

            // Feature 4
            p.DomEvents100ms = d.DomCnt100ms;
            p.TradeEvents100ms = d.TradeCnt100ms;
            p.CancelEvents100ms = d.CancelCnt100ms;

            p.MidVel   = d.MidVel;
            p.MidAccel = d.MidAccel;

            p.MidVelRaw = d.MidVel;
            p.MidAccelRaw = d.MidAccel;
            p.SpreadVelRaw = d.SpreadVel;

            p.TradeImb1s = d.TradeImb1s;
            p.SessionVol = d.SessionVol;
            p.SessionVWAP = d.SessionVWAP;
            p.BidRefillRate = d.BidRefillRate;
            p.AskRefillRate = d.AskRefillRate;

            p.LabelEligibleLive =
                p.ExecFeasible &&
                p.Latency <= 200;

            if (d.Symbol.StartsWith("ES")) LatestEsSeq = d.Seq;
            if (d.Symbol.StartsWith("NQ")) LatestNqSeq = d.Seq;

            if (tag == Tags.TRADE) p.TradeVol = tradeVol;
            else
            {
                p.Bid = d.CurrentBidPx;
                p.Ask = d.CurrentAskPx;
                p.TouchBid = d.CurrentTouchBid;
                p.TouchAsk = d.CurrentTouchAsk;
                p.SumBid = d.CurrentSumBid;
                p.SumAsk = d.CurrentSumAsk;
                p.ConcBid = d.CurrentConcBid;
                p.ConcAsk = d.CurrentConcAsk;
                p.VoidBid = d.CurrentVoidBid;
                p.VoidAsk = d.CurrentVoidAsk;
                p.GapBid = d.CurrentGapBid;
                p.GapAsk = d.CurrentGapAsk;
                p.Micro = d.CurrentMicro;
                p.Ofi = isStructural ? 0 : d.CurrentOfi;
                p.DepthOfi = d.CurrentDepthOfi;
                p.ChurnK = d.CurrentChurnAdded + d.CurrentChurnCanceled;
                p.CancelK = (d.CurrentChurnAdded+d.CurrentChurnCanceled > 0) ? (double)d.CurrentChurnCanceled / (d.CurrentChurnAdded+d.CurrentChurnCanceled) : 0.0;
                p.Pressure = d.PressureEma;
                p.Pressure1 = d.PrevActivePressure;
                p.QImb = d.LastActiveQimb;
                p.QImb1 = d.PrevActiveQimb;
                p.Spoof = d.PendingSpoofFlag;
                p.Iceberg = d.PendingIcebergFlag;
                p.XB = xb;
                FillDepth(ref p, d, bid, ask);
            }

            // Enqueue after atomic allocation
            d.SnapshotQueue.Enqueue(p);
            d.LastEventUtc = nowUtc;

            if (eventTime > d.LastEmittedExchTs) d.LastEmittedExchTs = eventTime;

            if (tag == Tags.DOM)
            {
                d.LastEmittedDomSeq = d.Seq;
                d.LastEmittedDomExchTs = eventTime;
            }

            if (tag == Tags.DOM)
            {
                lock (d.HistoryLock)
                {
                    if (d.HistoryCount > 0)
                    {
                        int lastIdx = (d.HistoryHead - 1 + HISTORY_BUFFER_SIZE) % HISTORY_BUFFER_SIZE;
                        if (Math.Abs((d.HistoryBuffer[lastIdx].ExchTs - eventTime).TotalMilliseconds) < 0.001)
                        {
                            d.HistoryBuffer[lastIdx].Seq = d.Seq;
                        }
                    }
                }
            }

            if (tag != Tags.TRADE)
            {
                d.LastWriteBidPx = d.CurrentBidPx;
                d.LastWriteAskPx = d.CurrentAskPx;
                d.LastWriteTouchBid = d.CurrentTouchBid;
                d.LastWriteTouchAsk = d.CurrentTouchAsk;
                d.LastWriteSumBid = d.CurrentSumBid;
                d.LastWriteSumAsk = d.CurrentSumAsk;
            }
        }

        private long ComputeDepthOfi(DomState d, int k, double effectiveBid, double effectiveAsk)
        {
            return 0; // Disabled: Requires previous state logic which was removed
        }

        private void ComputeChurn(DomState d, int k, double effectiveBid, double effectiveAsk, out long added, out long canceled)
        {
            added = 0;
            canceled = 0;
            // Disabled
        }

        private void UpdateSpoof(DomState d, DateTime nowUtc, bool isBidSide, double price, long delta, double bestBid, double bestAsk)
        {
            if (TickSize <= 0) return;
            double touch = isBidSide ? bestBid : bestAsk;
            if (touch <= 0) return;
            double distTicks = Math.Abs(price - touch) / TickSize;
            if (distTicks > SpoofMaxTicksFromTouch) return;

            var map = isBidSide ? d.SpoofBid : d.SpoofAsk;

            if (map.Count > 0)
            {
                var toRemove = new List<double>();
                foreach (var kv in map)
                {
                    if ((nowUtc - kv.Value.AddUtc).TotalMilliseconds > SpoofWindowMs)
                        toRemove.Add(kv.Key);
                }
                for (int i = 0; i < toRemove.Count; i++) map.Remove(toRemove[i]);
            }

            if (delta >= SpoofMinAddSize)
            {
                map[price] = new SpoofCandidate { Price = price, AddSize = delta, AddUtc = nowUtc };
                return;
            }

            if (delta <= 0 && map.ContainsKey(price))
            {
                var c = map[price];
                double ageMs = (nowUtc - c.AddUtc).TotalMilliseconds;
                if (ageMs <= SpoofWindowMs)
                {
                    long cancelSize = -delta;
                    if (cancelSize >= (long)Math.Round(c.AddSize * SpoofCancelFrac))
                    {
                        d.LastSpoofFlag = 1;
                        map.Remove(price);
                    }
                }
            }
        }

        private void UpdateIcebergFromDepth(DomState d, DateTime nowUtc, int liqSide, double price, long liqDelta)
        {
            if (!d.Pending.Active) return;
            if ((nowUtc - d.Pending.Utc).TotalMilliseconds > IcebergWindowMs)
            {
                d.Pending.Active = false;
                return;
            }
            if (Math.Abs(price - d.Pending.Price) > TickSize * 0.00001) return;

            long tradeVol = d.Pending.Vol;
            if (tradeVol < IcebergMinTradeVol) { d.Pending.Active = false; return; }

            long visibleChange = -liqDelta; // oldVol - newVol = -(newVol - oldVol) = -delta
            bool isRelevantSide = (d.Pending.Side > 0 && liqSide == -1) || (d.Pending.Side < 0 && liqSide == 1);
            // Pending.Side > 0 is Buy Aggressor (lifted offer) -> matches Ask side updates (liqSide -1)
            // Pending.Side < 0 is Sell Aggressor (hit bid) -> matches Bid side updates (liqSide 1)

            if (!isRelevantSide) return;

            double frac = (tradeVol > 0) ? (visibleChange / (double)tradeVol) : 0.0;
            if (visibleChange <= 0 || frac < IcebergMinVisibleFillFrac) d.LastIcebergFlag = 1;
            d.Pending.Active = false;
        }

        private static void OpenFusedFile()
        {
            try
            {
                string baseDir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "A5_DOM_Data_v309", "FUSED");
                if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);

                string today = NextGlobalUtc().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (today != fusedDate || fusedWriter == null)
                {
                    if (fusedWriter != null) { fusedWriter.Flush(); fusedWriter.Close(); }

                    if (today != fusedDate) FusedPart = 1;
                    fusedDate = today;

                    string fname = $"FUSED_{fusedDate}";
                    if (FusedPart > 1) fname += $"_part{FusedPart}";
                    string path = Path.Combine(baseDir, fname + ".jsonl");

                    bool isNew = !File.Exists(path);
                    // Patch 7: 1MB Buffer for High Throughput
                    fusedWriter = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20),
                        new UTF8Encoding(false),
                        1 << 20);
                    fusedWriter.AutoFlush = false;

                    if(isNew) {
                         string header = $"{{\"tag\":\"metadata\",\"type\":\"header\",\"version\":\"A5_DOM_MultiDepth_AI_v309_ML_LOCKED\",\"date\":\"{fusedDate}\"}}";
                         fusedWriter.WriteLine(header);
                         // Now calling static BuildFusedSessionStart
                         string sessionStart = BuildFusedSessionStart();
                         fusedWriter.WriteLine(sessionStart);
                    }
                }
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process($"[A5] OpenFusedFile failed: {ex.Message}", PrintTo.OutputTab1);
            }
        }

        private static void TryRestartFusedWriter()
        {
            if (Interlocked.CompareExchange(ref fusedWriterRunning, 1, 0) != 0)
                return;

            try
            {
                fusedWriter?.Flush();
                fusedWriter?.Dispose();

                OpenFusedFile(); // your existing file init

                fusedLastHeartbeatTicks = Stopwatch.GetTimestamp();
                fusedLastWriteTicks = fusedLastHeartbeatTicks;

                Task.Run(FusedWriterLoop);
            }
            catch (Exception ex)
            {
                NinjaTrader.Code.Output.Process("[A5] Restart failed: " + ex.Message, PrintTo.OutputTab1);
                Interlocked.Exchange(ref fusedWriterRunning, 0);
            }
        }

        private void FillDepth(ref LightDomPayload p, DomState d, double effectiveBid, double effectiveAsk)
        {
            int lvl = 0;
            for (int i = 0; i < DomState.DEPTH; i++)
            {
                if (lvl >= 10) break;
                if (double.IsNaN(d.BidPx[i])) break;
                if (d.BidPx[i] > effectiveBid + (TickSize * 0.00001)) continue;
                long delta = 0;

                switch(lvl) {
                    case 0: p.BP0=d.BidPx[i]; p.BS0=d.BidSz[i]; p.BD0=delta; break;
                    case 1: p.BP1=d.BidPx[i]; p.BS1=d.BidSz[i]; p.BD1=delta; break;
                    case 2: p.BP2=d.BidPx[i]; p.BS2=d.BidSz[i]; p.BD2=delta; break;
                    case 3: p.BP3=d.BidPx[i]; p.BS3=d.BidSz[i]; p.BD3=delta; break;
                    case 4: p.BP4=d.BidPx[i]; p.BS4=d.BidSz[i]; p.BD4=delta; break;
                    case 5: p.BP5=d.BidPx[i]; p.BS5=d.BidSz[i]; p.BD5=delta; break;
                    case 6: p.BP6=d.BidPx[i]; p.BS6=d.BidSz[i]; p.BD6=delta; break;
                    case 7: p.BP7=d.BidPx[i]; p.BS7=d.BidSz[i]; p.BD7=delta; break;
                    case 8: p.BP8=d.BidPx[i]; p.BS8=d.BidSz[i]; p.BD8=delta; break;
                    case 9: p.BP9=d.BidPx[i]; p.BS9=d.BidSz[i]; p.BD9=delta; break;
                }
                lvl++;
            }

            lvl = 0;
            for (int i = 0; i < DomState.DEPTH; i++)
            {
                if (lvl >= 10) break;
                if (double.IsNaN(d.AskPx[i])) break;
                if (d.AskPx[i] < effectiveAsk - (TickSize * 0.00001)) continue;
                long delta = 0;

                switch(lvl) {
                    case 0: p.AP0=d.AskPx[i]; p.AS0=d.AskSz[i]; p.AD0=delta; break;
                    case 1: p.AP1=d.AskPx[i]; p.AS1=d.AskSz[i]; p.AD1=delta; break;
                    case 2: p.AP2=d.AskPx[i]; p.AS2=d.AskSz[i]; p.AD2=delta; break;
                    case 3: p.AP3=d.AskPx[i]; p.AS3=d.AskSz[i]; p.AD3=delta; break;
                    case 4: p.AP4=d.AskPx[i]; p.AS4=d.AskSz[i]; p.AD4=delta; break;
                    case 5: p.AP5=d.AskPx[i]; p.AS5=d.AskSz[i]; p.AD5=delta; break;
                    case 6: p.AP6=d.AskPx[i]; p.AS6=d.AskSz[i]; p.AD6=delta; break;
                    case 7: p.AP7=d.AskPx[i]; p.AS7=d.AskSz[i]; p.AD7=delta; break;
                    case 8: p.AP8=d.AskPx[i]; p.AS8=d.AskSz[i]; p.AD8=delta; break;
                    case 9: p.AP9=d.AskPx[i]; p.AS9=d.AskSz[i]; p.AD9=delta; break;
                }
                lvl++;
            }
        }

        private void WriteLatencyStats(DomState d)
        {
            if (d.LatencyCount == 0) return;
            try
            {
                // Simple histogram approximation for p50/p95
                long p50Thresh = (long)(d.LatencyCount * 0.5);
                long p95Thresh = (long)(d.LatencyCount * 0.95);
                long accum = 0;
                int p50 = 0;
                int p95 = 0;

                for(int i=0; i<d.LatencyHistogram.Length; i++)
                {
                    accum += d.LatencyHistogram[i];
                    if (p50 == 0 && accum >= p50Thresh) p50 = i;
                    if (p95 == 0 && accum >= p95Thresh) p95 = i;
                    if (accum >= d.LatencyCount) break;
                }

                // Store p95 for admission control
                d.LatencyP95 = p95;

                string json = $"{{\"tag\":\"metadata\",\"type\":\"latency_stats\",\"ts\":\"{d.NextHostUtc().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}\",\"latency_ms\":{{\"p50\":{p50},\"p95\":{p95},\"max\":{d.LatencyMax},\"count\":{d.LatencyCount},\"source\":\"non_colocated\"}}}}\n";
                d.Writer.Write(json);
                d.Writer.Flush();
            }
            catch {}
        }

        private void FormatPayload(StringBuilder sb, ref LightDomPayload p, string symbol)
        {
            sb.Append('{');
            bool c = false;

            AppendKV(sb, ref c, "inst", symbol, quote: true);
            AppendKV(sb, ref c, "schema", "v309", quote: true);
            AppendKV(sb, ref c, "ts", p.HostUtc.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), quote: true);
            AppendKV(sb, ref c, "exch_ts", p.ExchTs.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), quote: true);
            AppendKV(sb, ref c, "raw_exch_ts", p.RawExchTs.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), quote: true);
            AppendKV(sb, ref c, "exch_regress", p.ExchRegress);
            AppendKV(sb, ref c, "delta_exch_ms", p.DeltaExchMs, decimals: 4);
            AppendKV(sb, ref c, "inst_seq", p.InstSeq);
            AppendKV(sb, ref c, "global_seq", p.GlobalSeq);
            AppendKV(sb, ref c, "seq", p.Seq);
            AppendKV(sb, ref c, "sellable", (!p.PolWarmup).ToString().ToLowerInvariant(), quote: false);

            string sTag = "UNKNOWN";
            if (p.Tag == Tags.DOM) sTag = "DOM";
            else if (p.Tag == Tags.TRADE) sTag = "TRADE";
            AppendKV(sb, ref c, "tag", sTag, quote: true);

            string sReason = "none";
            switch(p.Reason) {
                case Reasons.TIMER: sReason="timer"; break;
                case Reasons.TRADE: sReason="trade"; break;
                case Reasons.QUOTE: sReason="quote"; break;
                case Reasons.VOL: sReason="vol"; break;
                case Reasons.RISK: sReason="risk"; break;
                case Reasons.BOOTSTRAP: sReason="bootstrap"; break;
                case Reasons.FATAL_CROSS: sReason="fatal_cross"; break;
                case Reasons.FORCED_PRE_TRADE: sReason="forced_pre_trade"; break;
                case Reasons.INSERT_EVENT: sReason="insert_event"; break;
                case Reasons.REWRITE_FIELDS: sReason="rewrite_fields"; break;
                case Reasons.GAP_FILL: sReason="gap_fill"; break;
                case Reasons.TIME_REGRESSION: sReason="time_regression_rejected"; break;
                case Reasons.PRE_TRADE_SYNC: sReason="pre_trade_sync"; break;
            }
            AppendKV(sb, ref c, "reason", sReason, quote: true);

            string sState = "active";
            if (p.State == States.IDLE) sState = "idle";
            else if (p.State == States.IMPACT) sState = "impact";
            AppendKV(sb, ref c, "state", sState, quote: true);

            string sLiq = "none";
            if (p.LiquidityEvent == LiqEvents.ADD) sLiq = "add";
            else if (p.LiquidityEvent == LiqEvents.CANCEL) sLiq = "cancel";
            else if (p.LiquidityEvent == LiqEvents.MODIFY) sLiq = "modify";
            AppendKV(sb, ref c, "liquidity_event", sLiq, quote: true);

            string sAggr = "NA";
            switch(p.Aggr) {
                case Aggressors.B: sAggr="B"; break;
                case Aggressors.S: sAggr="S"; break;
                case Aggressors.M: sAggr="M"; break;
                case Aggressors.U: sAggr="U"; break;
            }
            AppendKV(sb, ref c, "aggr", sAggr, quote: true);

            if(c) sb.Append(','); c=true;
            sb.Append("\"raw_state\":{");
            bool cSub = false;
            AppendKV(sb, ref cSub, "crossed", p.Crossed ? "true" : "false", quote: false);
            AppendKV(sb, ref cSub, "locked", p.Locked ? "true" : "false", quote: false);
            AppendKV(sb, ref cSub, "raw_bid", p.RawBid);
            AppendKV(sb, ref cSub, "raw_ask", p.RawAsk);
            // 🔥 NEW — OPTIONAL FIELDS
            AppendKV(sb, ref cSub, "bid_size", p.L1Bid);
            AppendKV(sb, ref cSub, "ask_size", p.L1Ask);
            if (p.IsTrade) {
                AppendKV(sb, ref cSub, "aggr_ticks", p.AggrTicks);
            }
            sb.Append("}");

            // Only write liquidity details if there is an actual event
            if (p.LiqSide != 0) {
                if (c) sb.Append(','); c=true;
                sb.Append("\"liq_detail\":{");
                bool cLiq = false;
                AppendKV(sb, ref cLiq, "side", p.LiqSide);      // 1 or -1
                AppendKV(sb, ref cLiq, "level", p.LiqLevel);    // 0-9
                AppendKV(sb, ref cLiq, "delta", p.LiqDelta);    // +Size or -Size
                sb.Append("}");
            }

            if(c) sb.Append(','); c=true;
            sb.Append("\"book_age\":{");
            cSub = false;
            AppendKV(sb, ref cSub, "ms", p.BookAgeMs);
            string sAge = "stale";
            if(p.BookAgeClass == BookAges.FRESH) sAge = "fresh";
            else if(p.BookAgeClass == BookAges.WARM) sAge = "warm";
            AppendKV(sb, ref cSub, "class", sAge, quote: true);
            sb.Append("}");

            AppendKV(sb, ref c, "dom_seq_ref", p.DomSeqRef);

            if(c) sb.Append(','); c=true;
            sb.Append("\"price_match\":{");
            cSub = false;
            AppendKV(sb, ref cSub, "exact", p.PxExact ? "true" : "false", quote: false);
            AppendKV(sb, ref cSub, "PxTicksDiff", p.PxTicksDiff);
            AppendKV(sb, ref cSub, "within_spread", p.PxWithinSpread ? "true" : "false", quote: false);
            sb.Append("}");

            AppendKV(sb, ref c, "causal_pass", p.CausalPass ? "true" : "false", quote: false);

            if(c) sb.Append(','); c=true;
            sb.Append("\"repair\":{");
            cSub = false;
            if (p.RepairType == RepairTypes.STRUCTURAL)
                AppendKV(sb, ref cSub, "id", $"R{p.Seq}-{symbol}", quote: true);

            string sRepType = "none";
            if(p.RepairType == RepairTypes.STRUCTURAL) sRepType="structural";
            else if(p.RepairType == RepairTypes.ANNOTATION) sRepType="annotation";
            else if(p.RepairType == RepairTypes.SOFT) sRepType="soft";
            AppendKV(sb, ref cSub, "type", sRepType, quote: true);

            string sRepReason = "none";
            switch(p.RepairReason) {
                case RepairReasons.GAP_FILL: sRepReason="gap_fill"; break;
                case RepairReasons.INSERT_EVENT: sRepReason="insert_event"; break;
                case RepairReasons.REWRITE_FIELDS: sRepReason="rewrite_fields"; break;
                case RepairReasons.DOM_AGE_UPDATE: sRepReason="dom_age_update"; break;
                case RepairReasons.CROSSED_BOOK: sRepReason="crossed_book"; break;
            }
            AppendKV(sb, ref cSub, "reason", sRepReason, quote: true);

            string sRepMethod = "none";
            if(p.RepairMethod == RepairMethods.SNAP_FORWARD) sRepMethod="snap_forward";
            AppendKV(sb, ref cSub, "method", sRepMethod, quote: true);

            string sRepRule = "NONE";
            if(p.RepairRuleId == RepairRules.TIME_SKEW_CHK) sRepRule="TIME_SKEW_CHK";
            else if(p.RepairRuleId == RepairRules.CB_FIX_HARD) sRepRule="CB_FIX_HARD";
            AppendKV(sb, ref cSub, "rule_id", sRepRule, quote: true);
            AppendKV(sb, ref cSub, "confidence", p.RepairConf);
            sb.Append("}");

            if(c) sb.Append(','); c=true;
            sb.Append("\"execution\":{");
            cSub = false;
            AppendKV(sb, ref cSub, "feasible", p.ExecFeasible ? "true" : "false", quote: false);

            if(cSub) sb.Append(','); cSub=true;
            sb.Append("\"blocked_by\":[");
            bool cBlock = false;
            if((p.ExecBlockedMask & 1) != 0) { if(cBlock) sb.Append(','); sb.Append("\"latency\""); cBlock=true; }
            if((p.ExecBlockedMask & 2) != 0) { if(cBlock) sb.Append(','); sb.Append("\"clock\""); cBlock=true; }
            if((p.ExecBlockedMask & 4) != 0) { if(cBlock) sb.Append(','); sb.Append("\"stale_book\""); cBlock=true; }
            if((p.ExecBlockedMask & 8) != 0) { if(cBlock) sb.Append(','); sb.Append("\"spread\""); cBlock=true; }
            sb.Append("]");

            AppendKV(sb, ref cSub, "latency_ms", p.ExecLatency);
            sb.Append("}");

            if(c) sb.Append(','); c=true;
            sb.Append("\"causality\":{");
            cSub = false;
            string sCaus = "none";
            switch(p.CausalityLabel) {
                case CausalLabels.PRE_TRADE: sCaus="pre_trade"; break;
                case CausalLabels.POST_TRADE: sCaus="post_trade"; break;
                case CausalLabels.NO_DOM_CONTEXT: sCaus="no_dom_context"; break;
                case CausalLabels.TIMEOUT: sCaus="timeout"; break;
            }
            AppendKV(sb, ref cSub, "label", sCaus, quote: true);
            AppendKV(sb, ref cSub, "window_ms", p.CausalityWindow);
            AppendKV(sb, ref cSub, "locked", p.CausalityLocked ? "true" : "false", quote: false);
            AppendKV(sb, ref cSub, "confidence", p.CausalityScore);
            sb.Append("}");

            if(c) sb.Append(','); c=true;
            sb.Append("\"policy\":{");
            cSub = false;
            AppendKV(sb, ref cSub, "warmup", p.PolWarmup ? "true" : "false", quote: false);
            AppendKV(sb, ref cSub, "rth", p.PolRth ? "true" : "false", quote: false);
            AppendKV(sb, ref cSub, "train_research", p.PolTrainRes ? "true" : "false", quote: false);
            AppendKV(sb, ref cSub, "train_exec", p.PolTrainExec ? "true" : "false", quote: false);
            AppendKV(sb, ref cSub, "exec_live", p.PolExecLive ? "true" : "false", quote: false);

            string sPol = "none";
            switch(p.PolReason) {
                case PolReasons.STRUCTURAL_REPAIR: sPol="structural_repair"; break;
                case PolReasons.CAUSAL_FAIL: sPol="causal_fail"; break;
                case PolReasons.LATENCY_GT_3S: sPol="latency_gt_3s"; break;
                case PolReasons.POLICY_FAIL: sPol="policy_fail"; break;
                case PolReasons.WIDE_SPREAD: sPol="wide_spread"; break;
                case PolReasons.EXCHANGE_BURST: sPol="exchange_burst"; break;
            }
            AppendKV(sb, ref cSub, "reason", sPol, quote: true);
            sb.Append("}");

            if(p.IsTrade)
            {
                if(c) sb.Append(','); c=true;
                sb.Append("\"causal_anchor\":{");
                cSub = false;
                if(p.AnchorDomSeq >= 0) AppendKV(sb, ref cSub, "dom_seq", p.AnchorDomSeq);
                else AppendKV(sb, ref cSub, "dom_seq", "null", quote: false);

                string sAnc = "none";
                if(p.AnchorType == AnchorTypes.STRICT_MATCH) sAnc="strict_match";
                else if(p.AnchorType == AnchorTypes.BEST_PRIOR) sAnc="best_prior";
                AppendKV(sb, ref cSub, "anchor_type", sAnc, quote: true);

                if(p.AnchorDomSeq >= 0)
                {
                    AppendKV(sb, ref cSub, "exch_ts_delta_ms", p.AnchorExchDelta);
                    AppendKV(sb, ref cSub, "host_ts_delta_ms", p.AnchorHostDelta);
                    AppendKV(sb, ref cSub, "confidence", p.AnchorConf);
                }

                string sAncReason = "";
                if(p.AnchorReason == AnchorReasons.EXCH_BATCH_AMBIGUITY) sAncReason="exchange_batch_ambiguity";
                else if(p.AnchorReason == AnchorReasons.NO_PRIOR_HISTORY) sAncReason="no_prior_history";

                if(!string.IsNullOrEmpty(sAncReason))
                    AppendKV(sb, ref cSub, "reason", sAncReason, quote: true);
                sb.Append("}");

                AppendKV(sb, ref c, "has_dom_context", p.HasDomContext ? "true" : "false", quote: false);
            }

            AppendKV(sb, ref c, "train_eligible", p.TrainEligible ? "true" : "false", quote: false);

            string sTrainBlock = "none";
            switch(p.TrainBlockReason) {
                case PolReasons.STRUCTURAL_REPAIR: sTrainBlock="structural_repair"; break;
                case PolReasons.CAUSAL_FAIL: sTrainBlock="causal_fail"; break;
                case PolReasons.LATENCY_GT_3S: sTrainBlock="latency_gt_3s"; break;
                case PolReasons.POLICY_FAIL: sTrainBlock="policy_fail"; break;
                case PolReasons.WIDE_SPREAD: sTrainBlock="wide_spread"; break;
                case PolReasons.EXCHANGE_BURST: sTrainBlock="exchange_burst"; break;
            }
            AppendKV(sb, ref c, "train_block_reason", sTrainBlock, quote: true);
            AppendKV(sb, ref c, "stable_ms", p.StableMs);

            if(p.IsTrade)
            {
                if(c) sb.Append(','); c=true;
                sb.Append("\"burst\":{");
                cSub = false;
                AppendKV(sb, ref cSub, "id", p.BurstId);
                AppendKV(sb, ref cSub, "cnt", p.BurstCnt);

                string sBurst = "none";
                if(p.BurstType == BurstTypes.SAME_EXCH_TS) sBurst="same_exch_ts";
                else if(p.BurstType == BurstTypes.PRICE_CLUSTER) sBurst="price_cluster";
                AppendKV(sb, ref cSub, "type", sBurst, quote: true);

                AppendKV(sb, ref cSub, "rule", "exchange_batch", quote: true);
                sb.Append("}");
            }

            if(c) sb.Append(','); c=true;
            sb.Append("\"clock\":{");
            cSub = false;
            AppendKV(sb, ref cSub, "stable_ms", p.StableMs);
            AppendKV(sb, ref cSub, "monotonic", p.ClockMonotonic ? "true" : "false", quote: false);
            sb.Append("}");

            AppendKV(sb, ref c, "dt_ms", p.DtMs);
            AppendKV(sb, ref c, "latency", p.Latency);

            if(c) sb.Append(','); c=true;
            sb.Append("\"last_px\":");
            if(p.LastPx > 0) AppendDouble(sb, p.LastPx); else sb.Append("null");

            AppendKV(sb, ref c, "bid", p.Bid);
            AppendKV(sb, ref c, "ask", p.Ask);
            AppendKV(sb, ref c, "bid_sz", p.L1Bid);
            AppendKV(sb, ref c, "ask_sz", p.L1Ask);
            AppendKV(sb, ref c, "spread", p.Spread);
            AppendKV(sb, ref c, "spread_ticks", p.SpreadTicks);

            if(!p.IsTrade)
            {
                AppendKV(sb, ref c, "touch_bidSz", p.TouchBid);
                AppendKV(sb, ref c, "touch_askSz", p.TouchAsk);
                AppendKV(sb, ref c, "sumBidK", p.SumBid);
                AppendKV(sb, ref c, "sumAskK", p.SumAsk);
                AppendKV(sb, ref c, "conc_bid", p.ConcBid, decimals: 4);
                AppendKV(sb, ref c, "conc_ask", p.ConcAsk, decimals: 4);
                AppendKV(sb, ref c, "void_bid", p.VoidBid ? "true" : "false", quote: false);
                AppendKV(sb, ref c, "void_ask", p.VoidAsk ? "true" : "false", quote: false);
                AppendKV(sb, ref c, "gap_bid", p.GapBid);
                AppendKV(sb, ref c, "gap_ask", p.GapAsk);
                AppendKV(sb, ref c, "micro", p.Micro, decimals: 6);
                AppendKV(sb, ref c, "ofi", p.Ofi);
                AppendKV(sb, ref c, "depth_ofi", p.DepthOfi);
                AppendKV(sb, ref c, "churn_k", p.ChurnK);
                AppendKV(sb, ref c, "cancel_k", p.CancelK, decimals: 4);
                AppendKV(sb, ref c, "pressure", p.Pressure, decimals: 4);
                AppendKV(sb, ref c, "pressure1", p.Pressure1, decimals: 4);
                AppendKV(sb, ref c, "qimb", p.QImb, decimals: 4);
                AppendKV(sb, ref c, "qimb1", p.QImb1, decimals: 4);
                AppendKV(sb, ref c, "spoof", p.Spoof);
                AppendKV(sb, ref c, "iceberg", p.Iceberg);
                AppendKV(sb, ref c, "xb", p.XB);

                AppendKV(sb, ref c, "imb_l1", p.ImbL1, 5);
                AppendKV(sb, ref c, "sweep_ticks", p.SweepTicks, 1);
                AppendKV(sb, ref c, "spread_bps", p.SpreadBps, 4);

                if (symbol.StartsWith("ES"))
                {
                    AppendKV(sb, ref c, "signal", p.Sig);
                    AppendKV(sb, ref c, "llr", p.Llr, 3);
                    AppendKV(sb, ref c, "lead_lag_ms", p.LeadLagMs, 2);
                    AppendKV(sb, ref c, "lead_lag_corr", p.LeadLagCorr, 3);
                    AppendKV(sb, ref c, "nq_z", p.NqZ, 2);
                    AppendKV(sb, ref c, "es_z", p.EsZ, 2);
                }

                AppendKV(sb, ref c, "rv_1s",  p.Rv1s,  decimals: 6);
                AppendKV(sb, ref c, "rv_5s",  p.Rv5s,  decimals: 6);
                AppendKV(sb, ref c, "rv_30s", p.Rv30s, decimals: 6);
                AppendKV(sb, ref c, "queue_pos_bid", p.QueuePosBid, decimals: 4);
                AppendKV(sb, ref c, "queue_pos_ask", p.QueuePosAsk, decimals: 4);
                AppendKV(sb, ref c, "ms_in_spread", p.MsInSpread);
                AppendKV(sb, ref c, "ofi_shock", p.OfiShock);
                AppendKV(sb, ref c, "mid_vel_ticks",   p.MidVelTicks,   decimals: 6);
                AppendKV(sb, ref c, "mid_accel_ticks", p.MidAccelTicks, decimals: 8);

                AppendKV(sb, ref c, "mid_vel_raw", p.MidVelRaw, decimals: 6);
                AppendKV(sb, ref c, "mid_accel_raw", p.MidAccelRaw, decimals: 8);
                AppendKV(sb, ref c, "spread_vel_raw", p.SpreadVelRaw, decimals: 6);

                AppendKV(sb, ref c, "market_regime", p.MarketRegime, quote: true);
                AppendKV(sb, ref c, "ofi_z", p.OfiZ, decimals: 4);
                AppendKV(sb, ref c, "structural_rate_1s", p.StructuralRate1s);

                AppendKV(sb, ref c, "top_flip_1s", p.TopFlipCount1s);
                AppendKV(sb, ref c, "spread_vel", p.SpreadVel, decimals: 6);
                AppendKV(sb, ref c, "ofi_skew_ema", p.OfiSkewEma, decimals: 4);
                AppendKV(sb, ref c, "cancel_add_ratio_1s", p.CancelAddRatio1s, decimals: 4);
                // Feature 2
                AppendKV(sb, ref c, "ms_since_trade",  p.MsSinceTrade);
                AppendKV(sb, ref c, "ms_since_top",    p.MsSinceTop);
                AppendKV(sb, ref c, "ms_since_spread", p.MsSinceSpread);

                // Feature 3
                AppendKV(sb, ref c, "pressure_slope", p.PressureSlope, decimals: 6);
                AppendKV(sb, ref c, "qimb_slope",     p.QimbSlope,     decimals: 6);

                // Feature 4
                AppendKV(sb, ref c, "dom_events_100ms",    p.DomEvents100ms);
                AppendKV(sb, ref c, "trade_events_100ms",  p.TradeEvents100ms);
                AppendKV(sb, ref c, "cancel_events_100ms", p.CancelEvents100ms);
                AppendKV(sb, ref c, "mid_vel",           p.MidVel, decimals: 6);
                AppendKV(sb, ref c, "mid_accel",         p.MidAccel, decimals: 8);
                AppendKV(sb, ref c, "trade_imb_1s",      p.TradeImb1s);
                AppendKV(sb, ref c, "session_vol",       p.SessionVol);
                AppendKV(sb, ref c, "session_vwap",      p.SessionVWAP, decimals: 2);
                AppendKV(sb, ref c, "bid_refill_rate",   p.BidRefillRate, decimals: 2);
                AppendKV(sb, ref c, "ask_refill_rate",   p.AskRefillRate, decimals: 2);
                AppendKV(sb, ref c, "label_eligible_live", p.LabelEligibleLive ? "true" : "false", quote: false);

                if(c) sb.Append(','); c=true;
                sb.Append("\"depth\":[");
                bool dFirst = true;

                if (p.BP0 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP0, p.BS0, p.BD0, 0); dFirst = false; }
                if (p.AP0 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP0, p.AS0, p.AD0, 0); dFirst = false; }
                if (p.BP1 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP1, p.BS1, p.BD1, 1); dFirst = false; }
                if (p.AP1 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP1, p.AS1, p.AD1, 1); dFirst = false; }
                if (p.BP2 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP2, p.BS2, p.BD2, 2); dFirst = false; }
                if (p.AP2 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP2, p.AS2, p.AD2, 2); dFirst = false; }
                if (p.BP3 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP3, p.BS3, p.BD3, 3); dFirst = false; }
                if (p.AP3 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP3, p.AS3, p.AD3, 3); dFirst = false; }
                if (p.BP4 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP4, p.BS4, p.BD4, 4); dFirst = false; }
                if (p.AP4 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP4, p.AS4, p.AD4, 4); dFirst = false; }
                if (p.BP5 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP5, p.BS5, p.BD5, 5); dFirst = false; }
                if (p.AP5 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP5, p.AS5, p.AD5, 5); dFirst = false; }
                if (p.BP6 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP6, p.BS6, p.BD6, 6); dFirst = false; }
                if (p.AP6 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP6, p.AS6, p.AD6, 6); dFirst = false; }
                if (p.BP7 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP7, p.BS7, p.BD7, 7); dFirst = false; }
                if (p.AP7 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP7, p.AS7, p.AD7, 7); dFirst = false; }
                if (p.BP8 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP8, p.BS8, p.BD8, 8); dFirst = false; }
                if (p.AP8 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP8, p.AS8, p.AD8, 8); dFirst = false; }
                if (p.BP9 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.BP9, p.BS9, p.BD9, 9); dFirst = false; }
                if (p.AP9 > 0) { if (!dFirst) sb.Append(','); AppendDepthLevel(sb, p.AP9, p.AS9, p.AD9, 9); dFirst = false; }

                sb.Append("]");
            }
            else
            {
                AppendKV(sb, ref c, "tv", p.TradeVol);
            }
            sb.Append('}');
        }

        private void AppendDepthLevel(StringBuilder sb, double px, long sz, long delta, int lvl)
        {
            sb.Append('[');
            AppendDouble(sb, px); sb.Append(',');
            sb.Append(sz); sb.Append(",0,");
            sb.Append(delta); sb.Append(",0,");
            sb.Append(lvl); sb.Append(']');
        }


        private static double ComputeQueuePos(
            DomState d,
            bool isBid,
            double touchPx,
            long touchSize,
            int maxLevels)
        {
            if (touchPx <= 0 || touchSize <= 0) return -1;

            long ahead = 0;
            int lvl = 0;
            var px = isBid ? d.BidPx : d.AskPx;
            var sz = isBid ? d.BidSz : d.AskSz;

            for (int i = 0; i < DomState.DEPTH; i++)
            {
                if (double.IsNaN(px[i])) break;

                if (isBid)
                {
                    if (px[i] <= touchPx) break;
                }
                else
                {
                    if (px[i] >= touchPx) break;
                }
                ahead += sz[i];
                if (++lvl >= maxLevels) break;
            }

            return Math.Min(1.0, ahead / (double)touchSize);
        }
        private static int ClampK(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static long GetLevelSize(DomState d, bool isBid, double price)
        {
            if (price <= 0) return 0;
            var px = isBid ? d.BidPx : d.AskPx;
            var sz = isBid ? d.BidSz : d.AskSz;

            for (int i = 0; i < DomState.DEPTH; i++)
            {
                if (double.IsNaN(px[i])) break;
                if (px[i] == price) return sz[i];
            }
            return 0;
        }

        private static long SumTop(DomState d, bool isBid, int k)
        {
            k = ClampK(k, 1, DomState.DEPTH);
            long sum = 0;
            var sz = isBid ? d.BidSz : d.AskSz;
            var px = isBid ? d.BidPx : d.AskPx;

            for (int i = 0; i < k; i++)
            {
                if (double.IsNaN(px[i])) break;
                sum += sz[i];
            }
            return sum;
        }

        private static double RatioSigned(long a, long b)
        {
            long den = a + b;
            if (den == 0) return 0.0;
            return (double)(a - b) / (double)den;
        }

        private static double Ratio01(long a, long b)
        {
            long den = a + b;
            if (den == 0) return 0.5;
            return (double)a / (double)den;
        }

        private static double GetBestBid(DomState d)
        {
            return double.IsNaN(d.BidPx[0]) ? 0.0 : d.BidPx[0];
        }

        private static double GetBestAsk(DomState d)
        {
            return double.IsNaN(d.AskPx[0]) ? 0.0 : d.AskPx[0];
        }


        private static void AppendKV(StringBuilder sb, ref bool comma, string key, string val, bool quote)
        {
            if (comma) sb.Append(',');
            comma = true;
            sb.Append('\"').Append(key).Append("\":");
            if (quote) sb.Append('\"').Append(val).Append('\"');
            else sb.Append(val);
        }

        private static void AppendKV(StringBuilder sb, ref bool comma, string key, long val)
        {
            if (comma) sb.Append(',');
            comma = true;
            sb.Append('\"').Append(key).Append("\":");
            sb.Append(val.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendKV(StringBuilder sb, ref bool comma, string key, int val)
        {
            if (comma) sb.Append(',');
            comma = true;
            sb.Append('\"').Append(key).Append("\":");
            sb.Append(val.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendKV(StringBuilder sb, ref bool comma, string key, double val)
        {
            if (comma) sb.Append(',');
            comma = true;
            sb.Append('\"').Append(key).Append("\":");
            AppendDouble(sb, val);
        }

        private static void AppendKV(StringBuilder sb, ref bool comma, string key, double val, int decimals)
        {
            if (comma) sb.Append(',');
            comma = true;
            sb.Append('\"').Append(key).Append("\":");
            sb.Append(val.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        }

        private static void AppendDouble(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) sb.Append("null");
            else sb.Append(v.ToString("0.##########", CultureInfo.InvariantCulture));
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private A5_DOM_MultiDepth_AI_v309_ML_LOCKED[] cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED;
		public A5_DOM_MultiDepth_AI_v309_ML_LOCKED A5_DOM_MultiDepth_AI_v309_ML_LOCKED(int maxDepth, int pressureLevels, int depthOfiLevels, int queueImbLevels, bool writeFused, bool useSessionFilter, int flushMs, int minSnapshotIntervalMs, string secondaryInstrument, int heartbeatSec, bool enableSpoof, int spoofWindowMs, int spoofMinAddSize, int spoofMaxTicksFromTouch, double spoofCancelFrac, bool enableIceberg, int icebergWindowMs, int icebergMinTradeVol, double icebergMinVisibleFillFrac)
		{
			return A5_DOM_MultiDepth_AI_v309_ML_LOCKED(Input, maxDepth, pressureLevels, depthOfiLevels, queueImbLevels, writeFused, useSessionFilter, flushMs, minSnapshotIntervalMs, secondaryInstrument, heartbeatSec, enableSpoof, spoofWindowMs, spoofMinAddSize, spoofMaxTicksFromTouch, spoofCancelFrac, enableIceberg, icebergWindowMs, icebergMinTradeVol, icebergMinVisibleFillFrac);
		}

		public A5_DOM_MultiDepth_AI_v309_ML_LOCKED A5_DOM_MultiDepth_AI_v309_ML_LOCKED(ISeries<double> input, int maxDepth, int pressureLevels, int depthOfiLevels, int queueImbLevels, bool writeFused, bool useSessionFilter, int flushMs, int minSnapshotIntervalMs, string secondaryInstrument, int heartbeatSec, bool enableSpoof, int spoofWindowMs, int spoofMinAddSize, int spoofMaxTicksFromTouch, double spoofCancelFrac, bool enableIceberg, int icebergWindowMs, int icebergMinTradeVol, double icebergMinVisibleFillFrac)
		{
			if (cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED != null)
				for (int idx = 0; idx < cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED.Length; idx++)
					if (cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx] != null && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].MaxDepth == maxDepth && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].PressureLevels == pressureLevels && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].DepthOfiLevels == depthOfiLevels && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].QueueImbLevels == queueImbLevels && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].WriteFused == writeFused && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].UseSessionFilter == useSessionFilter && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].FlushMs == flushMs && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].MinSnapshotIntervalMs == minSnapshotIntervalMs && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].SecondaryInstrument == secondaryInstrument && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].HeartbeatSec == heartbeatSec && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].EnableSpoof == enableSpoof && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].SpoofWindowMs == spoofWindowMs && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].SpoofMinAddSize == spoofMinAddSize && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].SpoofMaxTicksFromTouch == spoofMaxTicksFromTouch && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].SpoofCancelFrac == spoofCancelFrac && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].EnableIceberg == enableIceberg && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].IcebergWindowMs == icebergWindowMs && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].IcebergMinTradeVol == icebergMinTradeVol && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].IcebergMinVisibleFillFrac == icebergMinVisibleFillFrac && cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx].EqualsInput(input))
						return cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED[idx];
			return CacheIndicator<A5_DOM_MultiDepth_AI_v309_ML_LOCKED>(new A5_DOM_MultiDepth_AI_v309_ML_LOCKED(){ MaxDepth = maxDepth, PressureLevels = pressureLevels, DepthOfiLevels = depthOfiLevels, QueueImbLevels = queueImbLevels, WriteFused = writeFused, UseSessionFilter = useSessionFilter, FlushMs = flushMs, MinSnapshotIntervalMs = minSnapshotIntervalMs, SecondaryInstrument = secondaryInstrument, HeartbeatSec = heartbeatSec, EnableSpoof = enableSpoof, SpoofWindowMs = spoofWindowMs, SpoofMinAddSize = spoofMinAddSize, SpoofMaxTicksFromTouch = spoofMaxTicksFromTouch, SpoofCancelFrac = spoofCancelFrac, EnableIceberg = enableIceberg, IcebergWindowMs = icebergWindowMs, IcebergMinTradeVol = icebergMinTradeVol, IcebergMinVisibleFillFrac = icebergMinVisibleFillFrac }, input, ref cacheA5_DOM_MultiDepth_AI_v309_ML_LOCKED);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.A5_DOM_MultiDepth_AI_v309_ML_LOCKED A5_DOM_MultiDepth_AI_v309_ML_LOCKED(int maxDepth, int pressureLevels, int depthOfiLevels, int queueImbLevels, bool writeFused, bool useSessionFilter, int flushMs, int minSnapshotIntervalMs, string secondaryInstrument, int heartbeatSec, bool enableSpoof, int spoofWindowMs, int spoofMinAddSize, int spoofMaxTicksFromTouch, double spoofCancelFrac, bool enableIceberg, int icebergWindowMs, int icebergMinTradeVol, double icebergMinVisibleFillFrac)
		{
			return indicator.A5_DOM_MultiDepth_AI_v309_ML_LOCKED(Input, maxDepth, pressureLevels, depthOfiLevels, queueImbLevels, writeFused, useSessionFilter, flushMs, minSnapshotIntervalMs, secondaryInstrument, heartbeatSec, enableSpoof, spoofWindowMs, spoofMinAddSize, spoofMaxTicksFromTouch, spoofCancelFrac, enableIceberg, icebergWindowMs, icebergMinTradeVol, icebergMinVisibleFillFrac);
		}

		public Indicators.A5_DOM_MultiDepth_AI_v309_ML_LOCKED A5_DOM_MultiDepth_AI_v309_ML_LOCKED(ISeries<double> input , int maxDepth, int pressureLevels, int depthOfiLevels, int queueImbLevels, bool writeFused, bool useSessionFilter, int flushMs, int minSnapshotIntervalMs, string secondaryInstrument, int heartbeatSec, bool enableSpoof, int spoofWindowMs, int spoofMinAddSize, int spoofMaxTicksFromTouch, double spoofCancelFrac, bool enableIceberg, int icebergWindowMs, int icebergMinTradeVol, double icebergMinVisibleFillFrac)
		{
			return indicator.A5_DOM_MultiDepth_AI_v309_ML_LOCKED(input, maxDepth, pressureLevels, depthOfiLevels, queueImbLevels, writeFused, useSessionFilter, flushMs, minSnapshotIntervalMs, secondaryInstrument, heartbeatSec, enableSpoof, spoofWindowMs, spoofMinAddSize, spoofMaxTicksFromTouch, spoofCancelFrac, enableIceberg, icebergWindowMs, icebergMinTradeVol, icebergMinVisibleFillFrac);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.A5_DOM_MultiDepth_AI_v309_ML_LOCKED A5_DOM_MultiDepth_AI_v309_ML_LOCKED(int maxDepth, int pressureLevels, int depthOfiLevels, int queueImbLevels, bool writeFused, bool useSessionFilter, int flushMs, int minSnapshotIntervalMs, string secondaryInstrument, int heartbeatSec, bool enableSpoof, int spoofWindowMs, int spoofMinAddSize, int spoofMaxTicksFromTouch, double spoofCancelFrac, bool enableIceberg, int icebergWindowMs, int icebergMinTradeVol, double icebergMinVisibleFillFrac)
		{
			return indicator.A5_DOM_MultiDepth_AI_v309_ML_LOCKED(Input, maxDepth, pressureLevels, depthOfiLevels, queueImbLevels, writeFused, useSessionFilter, flushMs, minSnapshotIntervalMs, secondaryInstrument, heartbeatSec, enableSpoof, spoofWindowMs, spoofMinAddSize, spoofMaxTicksFromTouch, spoofCancelFrac, enableIceberg, icebergWindowMs, icebergMinTradeVol, icebergMinVisibleFillFrac);
		}

		public Indicators.A5_DOM_MultiDepth_AI_v309_ML_LOCKED A5_DOM_MultiDepth_AI_v309_ML_LOCKED(ISeries<double> input , int maxDepth, int pressureLevels, int depthOfiLevels, int queueImbLevels, bool writeFused, bool useSessionFilter, int flushMs, int minSnapshotIntervalMs, string secondaryInstrument, int heartbeatSec, bool enableSpoof, int spoofWindowMs, int spoofMinAddSize, int spoofMaxTicksFromTouch, double spoofCancelFrac, bool enableIceberg, int icebergWindowMs, int icebergMinTradeVol, double icebergMinVisibleFillFrac)
		{
			return indicator.A5_DOM_MultiDepth_AI_v309_ML_LOCKED(input, maxDepth, pressureLevels, depthOfiLevels, queueImbLevels, writeFused, useSessionFilter, flushMs, minSnapshotIntervalMs, secondaryInstrument, heartbeatSec, enableSpoof, spoofWindowMs, spoofMinAddSize, spoofMaxTicksFromTouch, spoofCancelFrac, enableIceberg, icebergWindowMs, icebergMinTradeVol, icebergMinVisibleFillFrac);
		}
	}
}

#endregion
