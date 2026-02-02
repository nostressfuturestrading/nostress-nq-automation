# A5 Recorder v309 - LOCKED CANDIDATE

**Freeze Date:** 2026-01-30
**Status:** PRODUCTION LOCKED
**Integrity Level:** INSTITUTIONAL (Causal Ring + Monotonic Clock)

## Configuration Manifest
- **Ring Buffer:** 262,144 (2^18)
- **IO Buffer:** 1,048,576 (1MB)
- **Thread Priority:** Highest
- **Clock:** Global Static Monotonic
- **Recovery:** Auto-Restart on >5s Stall
- **Integrity:** SHA1 Session Hashing Enabled

## Operational Rules
1. Never modify `A5_DOM_MultiDepth_AI_v309_ML_LOCKED.cs` without incrementing version to v310.
2. Never open FUSED files while recording.
3. Monitor `SYSTEM_HEALTH` tags for `ring_pct > 50`.
