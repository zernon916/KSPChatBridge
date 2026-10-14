LAND2 deferred per the requested two-failure rule. Both Release builds succeeded, but both full-suite runs failed the existing KSC cross-runway approach simulation (Program.cs centerline intercept check). The attempted stricter route progression plus 30-degree cap stalled on arc legs. All LAND2 code/test changes were discarded with git checkout --. LAND1 remains intact. Route progression still requires a geometry-aware repair; no successful landing validation is claimed.

LAND5: Added pure final-guidance replays for default and generated chart inputs, elevations 0/70 m, and offsets +/-50 m, plus the below-flare alignment gate. These cover final-guidance math and chart serialization; they do not substitute for native LEARN handoff or live stock/TestPlate1 flights. No KSP flight was executed. LAND2 route progression remains deferred.

EXTRA1 bank cap deferred after two full-suite failures, per the requested rule. Both builds passed. With consistent 30-degree planning/command limits the existing 90-degree join replay circles repeatedly (2468 degrees of accumulated turning; requirement <720). All cap/lead changes and its test were discarded. The requested 30-degree cap is NOT present in the retained code; route geometry must be repaired first. Existing pre-change landing turn limits remain.

EXTRA2 range guard deferred after two full-suite failures. Builds passed; requesting land while setup was suspended outside the range boundary did not enter flyhome. All range-guard changes/tests discarded per instruction. No 140 km limit is present in the retained build.

LAND2 retry: proximity plus along-track capture failed the existing 90-degree join replay twice (excess circling). Both builds passed. Retry discarded per instruction; route skipping remains unresolved.
