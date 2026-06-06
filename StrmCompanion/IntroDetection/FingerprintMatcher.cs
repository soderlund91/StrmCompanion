using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Logging;

namespace StrmCompanion.IntroDetection
{
    public class FingerprintMatcher
    {
        private readonly ILogger _logger;
        private readonly int _hammingThreshold;
        private readonly int _minimumIntroSeconds;
        private readonly double _minAlignmentScore;
        private readonly int _maximumIntroSeconds;
        private readonly double _minEpisodeMatchFraction;

        // A detected start more than this many seconds from the median is an outlier.
        private const double MaxStartDeviation = 45.0;

        public FingerprintMatcher(
            ILogger logger,
            int hammingThreshold = 8,
            int minimumIntroSeconds = 10,
            double minAlignmentScore = 0.55,
            int maximumIntroSeconds = 300,
            int minEpisodeMatchPercent = 40)
        {
            _logger = logger;
            _hammingThreshold = hammingThreshold;
            _minimumIntroSeconds = minimumIntroSeconds;
            _minAlignmentScore = minAlignmentScore;
            _maximumIntroSeconds = maximumIntroSeconds;
            _minEpisodeMatchFraction = minEpisodeMatchPercent / 100.0;
        }

        private class EpisodeMatch
        {
            public long EpisodeId { get; set; }
            public string EpisodeName { get; set; }
            public double EpisodeStart { get; set; }   // seconds into this episode
            public double ReferenceStart { get; set; } // seconds into the reference
            public double DetectedLength { get; set; }
            public double AlignmentScore { get; set; }
        }

        private class ConsensusResult
        {
            public FingerprintData Reference        { get; set; }
            public List<EpisodeMatch> Matches       { get; set; }
            public List<FingerprintData> Unmatched  { get; set; }
            public double ConsensusLength           { get; set; }
            public double RefConsensusStart         { get; set; }
        }

        /// <summary>
        /// Finds the common intro for each episode using a multi-reference strategy.
        ///
        /// Validation pipeline per reference candidate:
        ///   1. Alignment quality: best-offset score must exceed MinAlignmentScore.
        ///   2. Run length: contiguous (gap-tolerant) match must span ≥ MinimumIntroLength.
        ///   3. Start consistency: detected start must be within MaxStartDeviation of the
        ///      median start (outlier rejection).
        ///   4. Quorum: at least MinEpisodeMatchFraction of episodes must contribute.
        ///   5. Consensus length clamped to MaximumIntroLengthSeconds.
        ///   6. Retry: unmatched episodes retried with relaxed threshold near consensus start.
        /// </summary>
        public Dictionary<long, IntroTimestamps> FindConsensusIntros(List<FingerprintData> episodes)
        {
            var result = new Dictionary<long, IntroTimestamps>();

            if (episodes.Count < 2)
            {
                _logger.Warn("StrmCompanion: need at least 2 episodes, got {0}", episodes.Count);
                return result;
            }

            // ── Multi-reference: try up to 3 candidate reference episodes ──────────
            // Picks whichever candidate yields the most matched episodes.
            int candidateCount = Math.Min(3, episodes.Count);
            ConsensusResult best = null;

            for (int refIdx = 0; refIdx < candidateCount; refIdx++)
            {
                _logger.Info("StrmCompanion: trying reference [{0}] '{1}'",
                    refIdx, episodes[refIdx].EpisodeName);

                var candidate = TryGetConsensus(episodes, refIdx);
                if (candidate == null) continue;

                if (best == null || candidate.Matches.Count > best.Matches.Count)
                    best = candidate;

                // Early exit: every non-reference episode already matched
                if (best.Matches.Count == episodes.Count - 1) break;
            }

            if (best == null)
            {
                _logger.Info("StrmCompanion: no reference candidate produced a quorum. Not writing any markers.");
                return result;
            }

            var reference    = best.Reference;
            double sps       = reference.SamplesPerSecond > 0 ? reference.SamplesPerSecond : 8.06;
            double consensusLen = best.ConsensusLength;
            double refStart     = best.RefConsensusStart;

            _logger.Info(
                "StrmCompanion: best reference '{0}' — consensus length={1:F1}s  ref-start={2:F1}s  ({3}/{4} episodes matched)",
                reference.EpisodeName, consensusLen, refStart, best.Matches.Count, episodes.Count - 1);

            // Reference episode timestamps
            result[reference.EpisodeInternalId] = new IntroTimestamps
            {
                StartSeconds = refStart,
                EndSeconds   = refStart + consensusLen
            };

            // Matched episode timestamps
            foreach (var m in best.Matches)
            {
                result[m.EpisodeId] = new IntroTimestamps
                {
                    StartSeconds = m.EpisodeStart,
                    EndSeconds   = m.EpisodeStart + consensusLen
                };
            }

            // ── Phase 5: retry unmatched episodes ─────────────────────────────────
            if (best.Unmatched.Count > 0)
            {
                int relaxedThreshold = Math.Min(_hammingThreshold + 6, 20);
                _logger.Info("StrmCompanion: retrying {0} unmatched episode(s) (threshold={1})",
                    best.Unmatched.Count, relaxedThreshold);

                foreach (var ep in best.Unmatched)
                {
                    var m = TryMatchNear(reference, ep, sps, relaxedThreshold, refStart);
                    if (m == null)
                    {
                        _logger.Info("StrmCompanion: '{0}' – no match even after retry", ep.EpisodeName);
                        continue;
                    }

                    if (Math.Abs(m.ReferenceStart - refStart) > MaxStartDeviation)
                    {
                        _logger.Info(
                            "StrmCompanion: '{0}' retry result rejected – ref start {1:F1}s is {2:F1}s from consensus {3:F1}s (outlier)",
                            ep.EpisodeName, m.ReferenceStart,
                            Math.Abs(m.ReferenceStart - refStart), refStart);
                        continue;
                    }

                    _logger.Info("StrmCompanion: '{0}' retry succeeded – start={1:F1}s", ep.EpisodeName, m.EpisodeStart);
                    result[ep.EpisodeInternalId] = new IntroTimestamps
                    {
                        StartSeconds = m.EpisodeStart,
                        EndSeconds   = m.EpisodeStart + consensusLen
                    };
                }
            }

            return result;
        }

        // ── Phases 1–4 for a given reference index ────────────────────────────────

        private ConsensusResult TryGetConsensus(List<FingerprintData> episodes, int refIdx)
        {
            var reference = episodes[refIdx];
            double sps = reference.SamplesPerSecond > 0 ? reference.SamplesPerSecond : 8.06;

            var matches   = new List<EpisodeMatch>();
            var unmatched = new List<FingerprintData>();

            // Phase 1: compare reference vs every other episode
            for (int i = 0; i < episodes.Count; i++)
            {
                if (i == refIdx) continue;

                var ep = episodes[i];
                var m  = TryMatch(reference, ep, sps, _hammingThreshold);

                if (m == null)
                {
                    _logger.Debug("StrmCompanion: '{0}' – no match against ref '{1}' (score too low or run too short)",
                        ep.EpisodeName, reference.EpisodeName);
                    unmatched.Add(ep);
                }
                else
                {
                    _logger.Debug(
                        "StrmCompanion: '{0}' start={1:F1}s len={2:F1}s score={3:P0} (ref '{4}')",
                        ep.EpisodeName, m.EpisodeStart, m.DetectedLength, m.AlignmentScore, reference.EpisodeName);
                    matches.Add(m);
                }
            }

            // Phase 2: outlier rejection on episode starts
            matches = RejectOutliers(matches);

            // Phase 3: quorum check
            // Bug fix: Math.Max(1,...) so 2-episode seasons can pass (previously Math.Max(2,...) made it impossible).
            int nonRef      = episodes.Count - 1;
            int minRequired = Math.Max(1, (int)Math.Ceiling(nonRef * _minEpisodeMatchFraction));
            if (matches.Count < minRequired)
            {
                _logger.Info(
                    "StrmCompanion: ref='{0}' only {1}/{2} episode(s) matched (need {3}). Quorum not met.",
                    reference.EpisodeName, matches.Count, nonRef, minRequired);
                return null;
            }

            // Phase 4: consensus
            double consensusLength   = Median(matches.Select(m => m.DetectedLength).ToList());
            double refConsensusStart = Median(matches.Select(m => m.ReferenceStart).ToList());

            // Clamp to MaximumIntroLengthSeconds (0 = no limit)
            if (_maximumIntroSeconds > 0 && consensusLength > _maximumIntroSeconds)
            {
                _logger.Info(
                    "StrmCompanion: clamping consensus length {0:F1}s → {1}s (MaximumIntroLengthSeconds)",
                    consensusLength, _maximumIntroSeconds);
                consensusLength = _maximumIntroSeconds;
            }

            return new ConsensusResult
            {
                Reference         = reference,
                Matches           = matches,
                Unmatched         = unmatched,
                ConsensusLength   = consensusLength,
                RefConsensusStart = refConsensusStart
            };
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>Full comparison: alignment score check + run detection.</summary>
        private EpisodeMatch TryMatch(
            FingerprintData reference, FingerprintData other,
            double sps, int hammingThreshold)
        {
            var a = reference.Fingerprint;
            var b = other.Fingerprint;
            int minSamples = (int)(_minimumIntroSeconds * sps);
            if (a.Count < minSamples || b.Count < minSamples) return null;

            // Use minSamples as window (not 2×) for better sensitivity on short intros.
            int windowSize = minSamples;
            int bestOffset = FindBestOffset(a, b, windowSize, out double bestScore);

            if (bestScore < _minAlignmentScore)
            {
                _logger.Info("StrmCompanion: '{0}' best alignment score {1:P0} < {2:P0} – no matching window found",
                    other.EpisodeName, bestScore, _minAlignmentScore);
                return null;
            }

            int aStart     = Math.Max(0, bestOffset);
            int bStart     = Math.Max(0, -bestOffset);
            int compareLen = Math.Min(a.Count - aStart, b.Count - bStart);
            if (compareLen < minSamples) return null;

            var run = FindBestRun(a, b, aStart, bStart, compareLen, hammingThreshold, sps);
            if (run == null || run.Value.length < minSamples) return null;

            double aIntroStart = (aStart + run.Value.start) / sps;
            double bIntroStart = (bStart + run.Value.start) / sps;
            double introLen    = run.Value.length / sps;

            return new EpisodeMatch
            {
                EpisodeId      = other.EpisodeInternalId,
                EpisodeName    = other.EpisodeName,
                EpisodeStart   = bIntroStart,
                ReferenceStart = aIntroStart,
                DetectedLength = introLen,
                AlignmentScore = bestScore
            };
        }

        /// <summary>
        /// Retry near the expected reference start position.
        /// Uses a narrower offset search so we don't pick up false positives elsewhere.
        /// </summary>
        private EpisodeMatch TryMatchNear(
            FingerprintData reference, FingerprintData other,
            double sps, int hammingThreshold, double refExpectedStart)
        {
            var a = reference.Fingerprint;
            var b = other.Fingerprint;
            int minSamples   = (int)(_minimumIntroSeconds * sps);
            if (a.Count < minSamples || b.Count < minSamples) return null;

            int searchRadius = (int)(45 * sps); // ±45 s search window
            int center       = (int)(refExpectedStart * sps);
            int windowSize   = minSamples;

            int bestOffset   = 0;
            double bestScore = -1;
            int lo = Math.Max(-(b.Count - windowSize), center - searchRadius);
            int hi = Math.Min(a.Count - windowSize,   center + searchRadius);

            int scanStride = Math.Max(1, windowSize / 2);

            for (int offset = lo; offset <= hi; offset++)
            {
                int aS = Math.Max(0, offset);
                int bS = Math.Max(0, -offset);
                int av = Math.Min(a.Count - aS, b.Count - bS);
                if (av < windowSize) continue;

                for (int scan = 0; scan + windowSize <= av; scan += scanStride)
                {
                    double s = SimilarityScore(a, b, aS + scan, bS + scan, windowSize);
                    if (s > bestScore) { bestScore = s; bestOffset = offset; }
                }
            }

            // Relax the score threshold slightly for retries
            if (bestScore < _minAlignmentScore - 0.10) return null;

            int aStart     = Math.Max(0, bestOffset);
            int bStart     = Math.Max(0, -bestOffset);
            int compareLen = Math.Min(a.Count - aStart, b.Count - bStart);
            if (compareLen < minSamples) return null;

            var run = FindBestRun(a, b, aStart, bStart, compareLen, hammingThreshold, sps);
            if (run == null || run.Value.length < minSamples) return null;

            return new EpisodeMatch
            {
                EpisodeId      = other.EpisodeInternalId,
                EpisodeName    = other.EpisodeName,
                EpisodeStart   = (bStart + run.Value.start) / sps,
                ReferenceStart = (aStart + run.Value.start) / sps,
                DetectedLength = run.Value.length / sps,
                AlignmentScore = bestScore
            };
        }

        /// <summary>
        /// Rejects episodes whose detected start is more than MaxStartDeviation seconds
        /// from the median. This catches wrong-position matches (e.g., matching the
        /// episode body instead of the intro).
        /// </summary>
        private List<EpisodeMatch> RejectOutliers(List<EpisodeMatch> matches)
        {
            if (matches.Count < 2) return matches;

            double medianStart = Median(matches.Select(m => m.EpisodeStart).ToList());
            var accepted = matches
                .Where(m => Math.Abs(m.EpisodeStart - medianStart) <= MaxStartDeviation)
                .ToList();

            int rejected = matches.Count - accepted.Count;
            if (rejected > 0)
                _logger.Info(
                    "StrmCompanion: rejected {0} episode(s) as outliers (start > {1}s from median {2:F1}s)",
                    rejected, MaxStartDeviation, medianStart);

            return accepted;
        }

        // ── Gap-tolerant run detection ────────────────────────────────────────────

        private (int start, int length)? FindBestRun(
            List<uint> a, List<uint> b,
            int aStart, int bStart, int compareLen,
            int hammingThreshold, double sps)
        {
            int maxGap    = Math.Max(1, (int)(0.5 * sps)); // 0.5 s gap tolerance
            int bestStart = -1, bestLen = 0;
            int runStart  = -1, runLen = 0, gapLen = 0;

            for (int i = 0; i < compareLen; i++)
            {
                bool match = HammingDistance(a[aStart + i], b[bStart + i]) < hammingThreshold;
                if (match)
                {
                    if (runStart < 0) runStart = i;
                    runLen += gapLen + 1;
                    gapLen  = 0;
                }
                else if (runStart >= 0)
                {
                    if (++gapLen > maxGap)
                    {
                        if (runLen > bestLen) { bestLen = runLen; bestStart = runStart; }
                        runStart = -1; runLen = 0; gapLen = 0;
                    }
                }
            }
            if (runStart >= 0 && runLen > bestLen) { bestLen = runLen; bestStart = runStart; }

            return bestStart >= 0 ? (bestStart, bestLen) : ((int, int)?)null;
        }

        // ── Offset search ─────────────────────────────────────────────────────────

        private int FindBestOffset(List<uint> a, List<uint> b, int windowSize, out double bestScore)
        {
            int bestOffset = 0;
            bestScore = -1;
            int maxSlide = Math.Max(a.Count, b.Count) - windowSize;
            if (maxSlide < 0) maxSlide = 0;

            // Stride of windowSize/2 ensures every point is covered by at least one window,
            // allowing intros that don't start at t=0 (e.g. after a cold open) to be found.
            int scanStride = Math.Max(1, windowSize / 2);

            for (int offset = -maxSlide; offset <= maxSlide; offset++)
            {
                int aS = Math.Max(0, offset);
                int bS = Math.Max(0, -offset);
                int av = Math.Min(a.Count - aS, b.Count - bS);
                if (av < windowSize) continue;

                for (int scan = 0; scan + windowSize <= av; scan += scanStride)
                {
                    double s = SimilarityScore(a, b, aS + scan, bS + scan, windowSize);
                    if (s > bestScore) { bestScore = s; bestOffset = offset; }
                }
            }
            return bestOffset;
        }

        private static double SimilarityScore(List<uint> a, List<uint> b, int aStart, int bStart, int length)
        {
            long total = 0;
            for (int i = 0; i < length; i++)
                total += HammingDistance(a[aStart + i], b[bStart + i]);
            return 1.0 - (double)total / ((long)length * 32);
        }

        private static int HammingDistance(uint a, uint b)
        {
            uint x = a ^ b;
            x -= (x >> 1) & 0x55555555u;
            x  = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
            x  = (x + (x >> 4)) & 0x0F0F0F0Fu;
            return (int)((x * 0x01010101u) >> 24);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var s = values.OrderBy(v => v).ToList();
            int m = s.Count / 2;
            return s.Count % 2 == 0 ? (s[m - 1] + s[m]) / 2.0 : s[m];
        }
    }
}
