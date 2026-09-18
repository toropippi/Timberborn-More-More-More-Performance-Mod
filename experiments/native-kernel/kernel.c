// Native travel-distance kernel for the 2026-09-17 experiment: how much faster
// is the need-behavior distance query outside Mono? Reads flat mirrors of the
// game's cached flow fields (open addressing, linear probing, empty key = -1).
// Build: x86_64-w64-mingw32-gcc -O2 -shared -static-libgcc -o t3mp_native.dll kernel.c
// Benchmark only (src/T3MPTestDriver/NativeKernelBench.cs); never shipped.
#include <stdint.h>
#include <math.h>

typedef struct {
    const int32_t *dirKeys, *dirVals; int32_t dirMask;      // node id -> field index
    const int32_t *fieldOffset, *fieldMask; const uint8_t *fieldFilled;
    const int32_t *keys; const float *vals;                   // all fields, one arena
} Cache;

static Cache g_road, g_terrain;
static int32_t g_sizeY, g_sizeZ;

static inline uint32_t slot(int32_t key, int32_t mask) { return (((uint32_t)key * 0x9E3779B1u) >> 8) & (uint32_t)mask; }

// 0 = miss, 1 = hit, 2 = field exists but is not filled.
static inline int lookup(const Cache *c, int32_t fieldNode, int32_t node, float *distance)
{
    uint32_t i = slot(fieldNode, c->dirMask);
    int32_t field = -1;
    for (;;) {
        int32_t k = c->dirKeys[i];
        if (k == fieldNode) { field = c->dirVals[i]; break; }
        if (k == -1) return 0;
        i = (i + 1) & (uint32_t)c->dirMask;
    }
    if (!c->fieldFilled[field]) return 2;
    int32_t mask = c->fieldMask[field];
    const int32_t *keys = c->keys + c->fieldOffset[field];
    uint32_t j = slot(node, mask);
    for (;;) {
        int32_t k = keys[j];
        if (k == node) { *distance = c->vals[c->fieldOffset[field] + j]; return 1; }
        if (k == -1) return 0;
        j = (j + 1) & (uint32_t)mask;
    }
}

// PathfindingService.FindPathUncached steps 1-4 (cached road, reversed road,
// cached terrain, reversed terrain). Returns 0 when the game would go on to the
// uncached searches or would try to fill a road field (left to managed code).
static inline int query(int32_t s, int32_t d, float *distance)
{
    int r = lookup(&g_road, s, d, distance);
    if (r == 1) return 1;
    if (r == 2) return 0;
    r = lookup(&g_road, d, s, distance);
    if (r == 1) return 1;
    if (r == 2) return 0;
    if (lookup(&g_terrain, s, d, distance) == 1) return 1;
    if (lookup(&g_terrain, d, s, distance) == 1) return 1;
    return 0;
}

// NodeIdService.WorldToId: grid = floor(x, z, y + 0.1), id over (size + boundary).
static inline int32_t node_id(float x, float y, float z)
{
    int32_t gx = (int32_t)floorf(x) + 1, gy = (int32_t)floorf(z) + 1, gz = (int32_t)floorf(y + 0.1f) + 1;
    return gx * g_sizeY * g_sizeZ + gy * g_sizeZ + gz;
}

__declspec(dllexport) void t3mp_init(int32_t which,
    const int32_t *dirKeys, const int32_t *dirVals, int32_t dirMask,
    const int32_t *fieldOffset, const int32_t *fieldMask, const uint8_t *fieldFilled,
    const int32_t *keys, const float *vals, int32_t sizeY, int32_t sizeZ)
{
    Cache *c = which == 0 ? &g_road : &g_terrain;
    c->dirKeys = dirKeys; c->dirVals = dirVals; c->dirMask = dirMask;
    c->fieldOffset = fieldOffset; c->fieldMask = fieldMask; c->fieldFilled = fieldFilled;
    c->keys = keys; c->vals = vals;
    g_sizeY = sizeY; g_sizeZ = sizeZ;
}

// One call per character: start -> each candidate (leg1) and candidate -> start
// (leg2). flags bit0/bit1 = leg resolved natively. Returns unresolved leg count.
__declspec(dllexport) int32_t t3mp_batch(float sx, float sy, float sz, const float *candidates, int32_t count,
    float *leg1, float *leg2, uint8_t *flags)
{
    int32_t s = node_id(sx, sy, sz), unresolved = 0;
    for (int32_t i = 0; i < count; i++) {
        int32_t d = node_id(candidates[3 * i], candidates[3 * i + 1], candidates[3 * i + 2]);
        uint8_t f = 0;
        if (query(s, d, &leg1[i])) f |= 1; else unresolved++;
        if (query(d, s, &leg2[i])) f |= 2; else unresolved++;
        flags[i] = f;
    }
    return unresolved;
}
