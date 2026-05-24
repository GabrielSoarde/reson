# M1 / T6 — Limiter Soft-Knee Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the post-mix hard-clamp limiter with a soft-knee, so loud/boosted material rolls off smoothly toward the ceiling instead of clipping into audible distortion.

**Architecture:** `LimiterSampleProvider` (the post-mix stage on the game output) currently hard-clamps each sample to `[-1, 1]`. Swap the clamp for a `tanh`-based soft knee above a threshold (default 0.98): samples below the threshold pass through untouched; above it, the excess is smoothly compressed and asymptotes to ±1.0, never exceeding it.

**Tech Stack:** .NET 8 (`Soundpad.*` namespace, brand "Reson"), NAudio, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-05-23-reson-roadmap-design.md` → Milestone 1, gap T6.

**Why first (before F1):** F1 (normalization) boosts quiet clips up to +12 dB, increasing pressure on the limiter. Landing T6 first means those boosts meet a graceful knee instead of a hard ceiling. T6 is schema-free and self-contained, so it has no dependency on F1.

---

## Design decisions (locked)

- **Curve:** for `|s| > T`, `out = sign(s) · (T + (1−T)·tanh(|s|−T))`. Continuous at `T` (evaluates to `T`), monotonic, and asymptotes to `±1.0` as `|s|→∞` — so the output is always strictly inside `[−1, 1]` above the knee and exactly the input below it.
  - **⚠️ Post-execution correction (2026-05-24):** this plan originally specified `tanh((|s|−T)/(1−T))` — the excess divided by the knee `K=1−T`. That is numerically broken at the locked threshold `T=0.98`, where `K=0.02`: dividing the excess by `0.02` drives the tanh argument to ≳16 for any realistic over-unit sample (e.g. peak 1.3), saturating tanh to `1.0` and recreating a hard **plateau at the ceiling** — the exact distortion T6 exists to remove. Verified numerically: the `/K` form yields only 14 distinct over-knee outputs and `maxabs = 1.0000000` on the test sine, **failing** `Continuous_Waveform_Rolls_Off_Without_Plateau`. The shipped `tanh(|s|−T)` form (raw excess, no `/K`) preserves every guarantee (continuous at T, monotonic, asymptotes to `T+K=1.0`) while staying smooth across the whole over-threshold range (55 distinct outputs, `maxabs ≈ 0.986`, passes). The deviation is effectively **forced**: with only `0.02` of headroom above `T`, no curve can be simultaneously continuous at `T`, asymptotic to `≤1.0`, *and* non-saturating unless the tanh argument is left unscaled. The implementer's interior runs in `double` (`Math.Tanh`) with a `MathF.BitDecrement(1f)` guard so float rounding can never emit exactly `1.0f`. This corrected curve is the one in `LimiterSampleProvider.cs` as of commit `2e162c9`.
- **Threshold: default `0.98` (deliberate, not the roadmap's example).** The roadmap wrote "soft-knee em 0.95", but 0.95 as a *protection* limiter is too aggressive: it would compress any clip whose peaks sit between 0.95 and 1.0 — clean, non-clipping material that didn't need touching — shaving 5% of the dynamic range off the top of every peaky sound, all the time. A protection limiter should only engage on material genuinely hitting the ceiling, so the threshold sits near 1.0. `0.98` leaves a 0.02 knee for the tanh to round off the last bit before 0 dBFS while leaving everything below 0.98 bit-exact. This is a conscious acoustic choice; 0.95 was an example number in the roadmap, not a validated decision. Injected via constructor so it's tunable/testable. No user-facing config field in T6 — YAGNI.
- **Position unchanged:** the limiter stays exactly where it is in the chain (post-mix, on the game output, before `WasapiOut`). Only the per-sample transfer function changes.
- **Below threshold is bit-exact pass-through:** normal-level material is untouched (no timbre change), same guarantee the hard-clamp gave for `|s| ≤ 1`.

---

## Task 1: Soft-knee transfer function

**Files:**
- Modify: `src/Soundpad/Audio/LimiterSampleProvider.cs`
- Test: `tests/Soundpad.Tests/Audio/LimiterSampleProviderTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

Create `tests/Soundpad.Tests/Audio/LimiterSampleProviderTests.cs`:

```csharp
using FluentAssertions;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class LimiterSampleProviderTests
{
    // ISampleProvider that replays a fixed buffer once.
    private sealed class ArraySource : ISampleProvider
    {
        private readonly float[] _data;
        private int _pos;
        public ArraySource(float[] data) { _data = data; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    private static float[] Limit(float[] input, double threshold)
    {
        var limiter = new LimiterSampleProvider(new ArraySource(input), threshold);
        var outBuf = new float[input.Length];
        int total = 0;
        while (total < input.Length)
        {
            int n = limiter.Read(outBuf, total, input.Length - total);
            if (n == 0) break;
            total += n;
        }
        return outBuf;
    }

    // Curve-shape tests use an explicit threshold of 0.5 — clean, exactly
    // representable in float, far from any epsilon-boundary fragility. The
    // shipping default (0.98) is verified separately in Default_Threshold_*.

    [Fact]
    public void Below_Threshold_Passes_Through_Bit_Exact()
    {
        // All strictly below 0.5, plus 0.5 itself (exactly representable in
        // float, so the <= boundary comparison is bit-safe).
        var input = new[] { 0f, 0.1f, -0.3f, 0.49f, 0.5f, -0.5f };
        var outp = Limit(input, threshold: 0.5);
        outp.Should().Equal(input);
    }

    [Fact]
    public void Above_Threshold_Is_Compressed_Strictly_Below_One()
    {
        var outp = Limit(new[] { 1.0f, 2.0f, 5.0f }, threshold: 0.5);
        foreach (var s in outp) s.Should().BeInRange(0.5f, 0.99999f); // above knee, never reaches 1.0
    }

    [Fact]
    public void Negative_Is_Symmetric()
    {
        var pos = Limit(new[] { 3.0f }, 0.5)[0];
        var neg = Limit(new[] { -3.0f }, 0.5)[0];
        neg.Should().BeApproximately(-pos, 1e-6f);
    }

    [Fact]
    public void Never_Exceeds_Unit_Range_For_Extreme_Input()
    {
        var outp = Limit(new[] { 100f, -100f, 10f, -10f }, 0.5);
        foreach (var s in outp) Math.Abs(s).Should().BeLessThanOrEqualTo(1.0f);
    }

    [Fact]
    public void Monotonic_Above_Threshold()
    {
        var a = Limit(new[] { 0.6f }, 0.5)[0];
        var b = Limit(new[] { 1.0f }, 0.5)[0];
        var c = Limit(new[] { 3.0f }, 0.5)[0];
        a.Should().BeLessThan(b);
        b.Should().BeLessThan(c);
    }

    [Fact]
    public void Continuous_At_Threshold()
    {
        // At T (=0.5, exact float) output is exactly T. Just above, output stays
        // very close to T — no jump. 0.5001f is a distinct float from 0.5f, and
        // the delta is well above float epsilon, so this isn't a tautology.
        var atT = Limit(new[] { 0.5f }, 0.5)[0];
        var justAbove = Limit(new[] { 0.5001f }, 0.5)[0];
        atT.Should().Be(0.5f);
        justAbove.Should().BeApproximately(0.5f, 0.01f);
        justAbove.Should().BeGreaterThan(atT); // strictly increasing past the knee
    }

    private static float ReadOne(LimiterSampleProvider lim)
    {
        var b = new float[1];
        lim.Read(b, 0, 1);
        return b[0];
    }

    [Fact]
    public void Default_Threshold_Is_Near_098()
    {
        // 0.97 is below the default 0.98 knee → untouched; 0.99 is above → compressed.
        ReadOne(new LimiterSampleProvider(new ArraySource(new[] { 0.97f }))).Should().Be(0.97f);
        var hot = ReadOne(new LimiterSampleProvider(new ArraySource(new[] { 0.99f })));
        hot.Should().BeInRange(0.98f, 0.99f);
        hot.Should().BeLessThan(0.99f); // compressed, not pass-through
    }

    [Fact]
    public void Continuous_Waveform_Rolls_Off_Without_Plateau()
    {
        // The case T6 exists for: a continuous waveform whose peaks exceed 1.0
        // after an F1 boost. A hard clamp would flat-top every over-unit sample
        // to exactly 1.0 (a plateau = audible distortion). The soft knee maps
        // each distinct input to a distinct output, so the crest stays curved.
        int rate = 48000, frames = rate / 100; // ~one 100 Hz cycle
        var sine = new float[frames];
        for (int i = 0; i < frames; i++)
            sine[i] = (float)(1.3 * Math.Sin(2 * Math.PI * i / frames)); // peak 1.3

        var outp = Limit(sine, threshold: 0.98);

        // (a) never exceeds the ceiling
        foreach (var s in outp) Math.Abs(s).Should().BeLessThanOrEqualTo(1.0f);

        // (b) the over-threshold region is NOT a flat plateau: the outputs from
        // inputs above 0.98 take many distinct values (a hard clamp would make
        // them all identical at 1.0).
        var overKnee = new List<float>();
        for (int i = 0; i < frames; i++)
            if (sine[i] > 0.98f) overKnee.Add(outp[i]);
        overKnee.Should().HaveCountGreaterThan(3);
        overKnee.Distinct().Count().Should().BeGreaterThan(overKnee.Count / 2); // mostly distinct, no plateau
    }
}
```


- [ ] **Step 2: Run, verify fails**

Run: `dotnet test --filter LimiterSampleProviderTests`
Expected: FAIL — the current `LimiterSampleProvider` has no `(source, threshold)` constructor, and hard-clamp would fail `Above_Threshold_Is_Compressed_Strictly_Below_One` (it returns exactly `1.0`) and `Continuous_Waveform_Rolls_Off_Without_Plateau` (it flat-tops the crest).

- [ ] **Step 3: Implement the soft knee**

Replace the body of `src/Soundpad/Audio/LimiterSampleProvider.cs`. Keep the namespace and the `ISampleProvider` shape; change the doc comment, add the threshold constructor param, and swap the clamp for the soft knee:

```csharp
using NAudio.Wave;

namespace Soundpad.Audio;

/// <summary>
/// Soft-knee limiter wrapping another <see cref="ISampleProvider"/>, on the
/// post-mix game output before WASAPI. Samples at or below <c>threshold</c>
/// (default 0.98) pass through bit-exact. Above it, the excess is smoothly
/// compressed via tanh and asymptotes to ±1.0, so output never reaches or
/// exceeds the ±1.0 ceiling and loud/boosted material rolls off instead of
/// hard-clipping into audible distortion.
/// </summary>
/// <remarks>
/// Transfer function for |s| &gt; T:  out = sign(s)·(T + (1−T)·tanh((|s|−T)/(1−T))).
/// Continuous at T, monotonic, bounded in (−1, 1) above the knee. Replaces the
/// earlier hard clamp (which prevented overflow but distorted on hot material —
/// the exact symptom this fixes, now that F1 normalization can boost +12 dB).
/// Default threshold is 0.98 (near the ceiling) so the limiter only engages on
/// material genuinely approaching 0 dBFS, leaving everything below untouched.
/// </remarks>
public sealed class LimiterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _threshold;
    private readonly float _knee; // 1 - threshold

    public LimiterSampleProvider(ISampleProvider source, double threshold = 0.98)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _threshold = (float)threshold;
        _knee = 1f - _threshold;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _source.Read(buffer, offset, count);
        int end = offset + n;
        for (int i = offset; i < end; i++)
        {
            float s = buffer[i];
            float mag = Math.Abs(s);
            if (mag <= _threshold) continue; // bit-exact pass-through
            float compressed = _threshold + _knee * MathF.Tanh((mag - _threshold) / _knee);
            buffer[i] = s < 0 ? -compressed : compressed;
        }
        return n;
    }
}
```

- [ ] **Step 4: Run, verify passes**

Run: `dotnet test --filter LimiterSampleProviderTests`
Expected: 8 passing (6 curve-shape + default-threshold + continuous-waveform).

- [ ] **Step 5: Update call sites if the constructor signature broke them**

The new constructor has a defaulted `threshold`, so existing `new LimiterSampleProvider(source)` calls still compile. Confirm:

Run: `dotnet build`
Expected: clean. If any call site passed a second arg before (none should), fix it.

- [ ] **Step 6: Run the full audio suite for regressions**

Run: `dotnet test --filter "FullyQualifiedName~Audio"`
Expected: all passing. Any prior test that asserted the limiter returns *exactly* `1.0` for an over-unit input (from the hard-clamp era) must be updated to the soft-knee expectation (`< 1.0`). Search: `grep -rn "Limiter\|1f\b" tests/Soundpad.Tests/Audio` and fix any such assertion.

- [ ] **Step 7: Commit**

```bash
git add src/Soundpad/Audio/LimiterSampleProvider.cs tests/Soundpad.Tests/Audio/LimiterSampleProviderTests.cs
git commit -m "feat(audio): soft-knee limiter (replaces hard clamp)"
```

---

## Self-review checklist (run after implementing)

- **Spec coverage:** T6 = "limiter soft-knee (configurável)". Threshold is a constructor param (configurable), default **0.98** — deliberately raised from the roadmap's example 0.95 (a protection limiter shouldn't compress clean peaks; see Design decisions). ✓
- **No overflow regression:** the soft knee asymptotes to ±1.0 and the tests assert output never exceeds the unit range for extreme input — same overflow guarantee the hard clamp gave. ✓
- **No timbre change at normal levels:** `Below_Threshold_Passes_Through_Bit_Exact` locks that material ≤ 0.95 is untouched. ✓
- **Type consistency:** single constructor `LimiterSampleProvider(ISampleProvider, double threshold = 0.95)`; `Read` signature unchanged.

## Notes for the implementer

- **Threshold is not user-configurable in T6.** It's a constructor default (0.95). If a future milestone wants a settings slider, the plumbing is one constructor arg away — but don't add the UI now (YAGNI).
- **Order vs F1:** ship this before F1 (per both plans' scope notes). After T6 + F1 both land, a +12 dB-boosted quiet clip whose peaks exceed 0 dBFS rolls off through the knee instead of clipping.
- **Monitor path:** the limiter is on the game output only (where mic + sound sum and overflow is possible). The monitor path plays a single sound at controlled gain and doesn't need it — leave it as is.
