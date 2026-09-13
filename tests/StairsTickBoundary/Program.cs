using System;
using System.Linq;
using T3MPTestDriver;

var cases = 0;
foreach (var bucketCount in new[] { 1, 2, 129 })
foreach (var target in new[] { 1, 2, 768 })
foreach (var batch in new[] { 1, 2, 128, 129, 130, 1024, 100000 })
{
    Check(bucketCount, target, _ => batch);
    cases++;
}
for (var seed = 0; seed < 20; seed++)
{
    var random = new Random(seed);
    Check(129, 768, _ => random.Next(0, 2049));
    cases++;
}
Console.WriteLine($"PASS {cases} batch schedules: every bucket processed exactly once per cycle, exact endpoint, parallel-completion observations only after all buckets.");

static void Check(int bucketCount, int target, Func<int, int> request)
{
    var boundary = new StairsTickBoundary(bucketCount, target);
    var visits = new int[bucketCount];
    var next = 0;
    var callbacks = 0;
    for (var frame = 0; boundary.CompletedTicks < target; frame++)
    {
        if (frame > bucketCount * target * 4) throw new Exception("Stalled test schedule");
        var batch = boundary.LimitBatch(request(frame), next);
        for (var i = 0; i < batch; i++)
        {
            visits[next]++;
            next = (next + 1) % bucketCount;
            if (boundary.AfterBucket(next))
            {
                callbacks++;
                if (visits.Any(n => n != callbacks)) throw new Exception("Observed a partially completed cycle");
            }
        }
    }
    if (next != 0 || callbacks != target || visits.Any(n => n != target)) throw new Exception("Incorrect final world boundary");
    if (boundary.LimitBatch(int.MaxValue, next) != 0) throw new Exception("More work was admitted after the endpoint");
}
