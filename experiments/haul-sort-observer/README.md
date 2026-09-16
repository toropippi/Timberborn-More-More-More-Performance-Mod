# Haul sort observer

Decision and evidence: [DECISIONS](../../docs/DECISIONS.md), `haul-sort`
in [evidence.json](../../docs/evidence.json).

This isolated observer always runs native sort and never returns captured
results to simulation. The normal test driver does not install it.
The recorded driver is in `testlogs/search-profile-20260913-113538/driver-archive/`.
Its flag is `-t3mpTestHaulSortObserve`; do not combine it with another search
profiler. Pure observer checks are in `tests/HaulSortObserver`.
