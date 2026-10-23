using BetterEssenceCorruptionHelper.Catalogue;
using System;
using System.Collections.Generic;

namespace BetterEssenceCorruptionHelper.Pricing
{
    /// <summary>What we know about one item's price.</summary>
    internal enum PriceState
    {
        /// <summary>Never sampled yet.</summary>
        NotSampled,

        /// <summary>Provider returned a positive, finite chaos value.</summary>
        Ok,

        /// <summary>
        /// Provider has market data but returned 0 for this item. Ninja Price returns 0 when the name is
        /// not in its poe.ninja data (e.g. low-volume tiers poe.ninja does not list) - and also when
        /// Essence valuation is disabled in its settings. It never reports a real price of 0.
        /// </summary>
        Unlisted,

        /// <summary>Provider is present but returned 0 for everything: its data has not loaded yet.</summary>
        ProviderLoading,

        /// <summary>The Ninja Price bridge method is not registered (plugin missing, disabled or not initialised).</summary>
        ProviderUnavailable,

        /// <summary>Provider returned NaN, infinity or a negative number.</summary>
        Invalid,

        /// <summary>The provider threw while pricing this item.</summary>
        Error,

        /// <summary>The latest sample failed; <see cref="PriceInfo.Chaos"/> is the last good value.</summary>
        Stale,
    }

    /// <summary>Overall state of the price provider after the latest sample.</summary>
    internal enum ProviderStatus
    {
        NotChecked,
        Unavailable,
        Loading,
        Ready,

        /// <summary>Currency prices came back but no essence did - Essence valuation disabled or the Essence feed failed.</summary>
        EssenceDataMissing,
    }

    /// <summary>
    /// One item's price. <see cref="Chaos"/> is the value of a single item (stack size 1) and is only
    /// meaningful when <see cref="HasValue"/>; missing, stale and invalid prices are distinct states,
    /// never a silent 0.
    /// </summary>
    internal readonly record struct PriceInfo(PriceState State, double Chaos, long SampledAtMs)
    {
        public static readonly PriceInfo NotSampled = new(PriceState.NotSampled, 0, 0);

        /// <summary>True when <see cref="Chaos"/> holds a usable price (fresh or last-known-good).</summary>
        public bool HasValue => State is PriceState.Ok or PriceState.Stale;

        public double? Value => HasValue ? Chaos : null;
    }

    /// <summary>Prices one item through the provider, in chaos per single item. May throw.</summary>
    internal delegate double PriceQuery(MarketItem item);

    /// <summary>
    /// A snapshot of Ninja Price's values for every catalogue item plus the Vaal Orb.
    ///
    /// <para>
    /// This is not a second price source: every value comes from Ninja Price's bridge, which reads
    /// Ninja Price's own in-memory poe.ninja data, and Ninja Price stays in charge of downloading and
    /// refreshing that data. The snapshot exists so the catalogue table (~100 rows) and the per-monster
    /// value ranges don't call the bridge for every row on every frame: one batch of ~105 calls is taken
    /// on the main thread every <see cref="RefreshIntervalMs"/>, sooner while the provider is missing or
    /// still loading (it initialises asynchronously, possibly after us), and immediately after
    /// <see cref="Invalidate"/> (area change, manual refresh).
    /// </para>
    ///
    /// <para>
    /// The provider delegate is resolved once and kept; it is only looked up again while it is missing,
    /// after every call through it failed, or on <see cref="Invalidate"/> with re-resolution (Ninja
    /// Price may have been reloaded and re-registered its method).
    /// </para>
    ///
    /// <para>Not thread-safe: owned by the main thread (Tick/Render/settings drawing).</para>
    /// </summary>
    internal sealed class PriceBook
    {
        public const long RefreshIntervalMs = 60_000;
        public const long RetryIntervalMs = 3_000;

        private readonly IReadOnlyList<MarketItem> _items;
        private readonly Dictionary<MarketItem, int> _indexByItem = new();
        private readonly PriceInfo[] _prices;
        private readonly Func<PriceQuery?> _resolveProvider;
        private readonly Func<long> _clockMs;

        private PriceQuery? _query;
        private long _nextSampleMs;

        public PriceBook(IReadOnlyList<MarketItem> items, Func<PriceQuery?> resolveProvider, Func<long> clockMs)
        {
            _items = items;
            _resolveProvider = resolveProvider;
            _clockMs = clockMs;
            _prices = new PriceInfo[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                _indexByItem[items[i]] = i;
                _prices[i] = PriceInfo.NotSampled;
            }
        }

        public IReadOnlyList<MarketItem> Items => _items;

        /// <summary>Incremented on every sample, so cached derived values know when to recompute.</summary>
        public int Version { get; private set; }

        public ProviderStatus Status { get; private set; } = ProviderStatus.NotChecked;

        /// <summary>Clock time of the latest sample, or null before the first one.</summary>
        public long? LastSampleMs { get; private set; }

        /// <summary>Wall time the latest sample took, in Stopwatch ticks (bridge cost, for diagnostics).</summary>
        public long LastSampleDurationTicks { get; private set; }

        public int ListedCount { get; private set; }
        public int UnlistedCount { get; private set; }
        public int FailedCount { get; private set; }

        public PriceInfo Get(MarketItem item) =>
            _indexByItem.TryGetValue(item, out var i) ? _prices[i] : PriceInfo.NotSampled;

        public PriceInfo Get(EssenceEntry entry) => Get(entry.Item);

        public PriceInfo VaalOrb => Get(EssenceCatalogue.VaalOrb);

        /// <summary>Price of one essence, or null when no usable price is known.</summary>
        public double? ValueOf(EssenceEntry entry) => Get(entry.Item).Value;

        /// <summary>Forces a sample on the next <see cref="Update"/>; optionally looks the provider up again.</summary>
        public void Invalidate(bool reResolveProvider)
        {
            _nextSampleMs = 0;
            if (reResolveProvider)
                _query = null;
        }

        /// <summary>Samples when due. Returns true when a sample was taken.</summary>
        public bool Update()
        {
            if (_clockMs() < _nextSampleMs)
                return false;

            Sample();
            return true;
        }

        /// <summary>Takes one sample of every item now.</summary>
        public void Sample()
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var now = _clockMs();

            _query ??= _resolveProvider();
            var query = _query;

            int listed = 0, unlisted = 0, failed = 0;
            var essenceListed = false;
            var anyPositive = false;

            if (query == null)
            {
                for (var i = 0; i < _prices.Length; i++)
                    _prices[i] = Degrade(_prices[i], PriceState.ProviderUnavailable, now);

                failed = _prices.Length;
                Status = ProviderStatus.Unavailable;
            }
            else
            {
                // First pass: query everything and keep the raw outcome. Zero is only classified once we
                // know whether the provider returned anything at all (loading vs. unlisted).
                var zeroes = new List<int>();
                for (var i = 0; i < _items.Count; i++)
                {
                    double raw;
                    try
                    {
                        raw = query(_items[i]);
                    }
                    catch
                    {
                        _prices[i] = Degrade(_prices[i], PriceState.Error, now);
                        failed++;
                        continue;
                    }

                    if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0)
                    {
                        _prices[i] = Degrade(_prices[i], PriceState.Invalid, now);
                        failed++;
                    }
                    else if (raw > 0)
                    {
                        _prices[i] = new PriceInfo(PriceState.Ok, raw, now);
                        listed++;
                        anyPositive = true;
                        essenceListed |= _items[i].Feed == MarketFeed.Essences;
                    }
                    else
                    {
                        zeroes.Add(i);
                    }
                }

                foreach (var i in zeroes)
                {
                    _prices[i] = anyPositive
                        ? new PriceInfo(PriceState.Unlisted, 0, now)
                        : Degrade(_prices[i], PriceState.ProviderLoading, now);
                }

                if (anyPositive)
                    unlisted = zeroes.Count;
                else
                    failed += zeroes.Count;

                Status = !anyPositive ? ProviderStatus.Loading
                    : !essenceListed ? ProviderStatus.EssenceDataMissing
                    : ProviderStatus.Ready;

                // Every call threw: the delegate is probably dead (provider unloaded). Look it up again.
                if (failed == _items.Count && listed == 0 && zeroes.Count == 0)
                {
                    _query = null;
                    Status = ProviderStatus.Unavailable;
                }
            }

            ListedCount = listed;
            UnlistedCount = unlisted;
            FailedCount = failed;
            LastSampleMs = now;
            _nextSampleMs = now + (Status == ProviderStatus.Ready ? RefreshIntervalMs : RetryIntervalMs);
            LastSampleDurationTicks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Version++;
        }

        /// <summary>
        /// A failed sample keeps the last good value, marked stale, rather than dropping to "no price".
        /// </summary>
        private static PriceInfo Degrade(PriceInfo previous, PriceState failure, long now) =>
            previous.HasValue
                ? new PriceInfo(PriceState.Stale, previous.Chaos, previous.SampledAtMs)
                : new PriceInfo(failure, 0, now);
    }
}
