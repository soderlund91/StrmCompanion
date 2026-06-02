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

        // Minimum fraction of episodes that must match before we trust the consensus.
        private const double MinEpisodeMatchFraction = 0.40; // at least 40 %

        // Minimum alignment-score at the best offset (0–1). Below this → reject.
        private const double MinAlignmentScore = 0.55;

        // A detected start more than this many seconds from the median is an outlier.
        private const double MaxStartDeviation = 45.0;

        public FingerprintMatcher(ILogger logger, int hammingThreshold = 8, int minimumIntroSeconds = 10)
        {
            _logger = logger;
            _hammingThreshold = hammingThreshold;
            _minimumIntroSeconds = minimumIntroSeconds;
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

        /// <summary>
        /// Finds the common intro for each episode.
        ///
        /// Validation pipeline (any failed step → episode is rejected or whole season fails):
        ///   1. Alignment quality: best-offset score must exceed MinAlignmentScore.
        ///   2. Run length: contiguous (gap-tolerant) match must span ≥ MinimumIntroLength.
        ///   3. Start consistency: detected start must be within MaxStartDeviation of the
        ///      median start (outlier rejection – catches wrong-position matches like ep.10).
        ///   4. Quorum: at least MinEpisodeMatchFraction of episodes must contribute to
        ///      the consensus before any timestamps are written.
        ///   5. Consensus length used as end: start + consensusLength per episode.
        ///      The per-episode end from fingerprinting is unreliable; the length is stable.
        ///   6. Retry: episodes that failed steps 1-2 are retried with a relaxed threshold
        ///      but ONLY accepted if their start is within MaxStartDeviation of the
        ///      consensus start estimate.
        /// </summary>
        public Dictionary<long, IntroTimestamps> FindConsensusIntros(List<FingerprintData> episodes)
        {
            var result   = new Dictionary<long, IntroTimestamps>();
            var unmatched = new List<FingerprintData>();

            if (episodes.Count < 2)
            {
                _logger.Warn("StrmCompanion: need at least 2 episodes, got {0}", episodes.Count);
                return result;
            }

            var reference = episodes[0];
            double sps = reference.SamplesPerSecond > 0 ? reference.SamplesPerSecond : 8.06;

            // ── Phase 1: compare reference vs each other episode ──────────────
            var matches = new List<EpisodeMatch>();

            for (int i = 1; i < episodes.Count; i++)
            {
                var ep = episodes[i];
                var m  = TryMatch(reference, ep, sps, _hammingThreshold);

                if (m == null)
                {
                    _logger.Debug("StrmCompanion: '{0}' – no match (score too low or run too short)",
                        ep.EpisodeName);
                    unmatched.Add(ep);
                }
                else
                {
                    _logger.Debug(
                        "StrmCompanion: '{0}' start={1:F1}s len={2:F1}s score={3:P0}",
                        ep.EpisodeName, m.EpisodeStart, m.DetectedLength, m.AlignmentScore);
                    matches.Add(m);
                }
            }

            // ── Phase 2: outlier rejection on episode starts ──────────────────
            // TV intros begin at roughly the same time in every episode.
            // A wildly different start means we found the wrong segment.
            matches = RejectOutliers(matches);

            // ── Phase 3: quorum check ─────────────────────────────────────────
            int minRequired = Math.Max(2, (int)Math.Ceiling((episodes.Count - 1) * MinEpisodeMatchFraction));
            if (matches.Count < minRequired)
            {
                _logger.Info(
                    "StrmCompanion: only {0}/{1} episode(s) matched (need {2}). " +
                    "Not writing any markers – confidence too low.",
                    matches.Count, episodes.Count - 1, minRequired);
                return result;
            }

            // ── Phase 4: consensus ────────────────────────────────────────────
            double consensusLength   = Median(matches.Select(m => m.DetectedLength).ToList());
            double refConsensusStart = Median(matches.Select(m => m.ReferenceStart).ToList());

            _logger.Info(
                "StrmCompanion: consensus length={0:F1}s  ref-start={1:F1}s  ({2}/{3} episodes)",
                consensusLength, refConsensusStart, matches.Count, episodes.Count - 1);

            // Reference episode
            result[reference.EpisodeInternalId] = new IntroTimestamps
            {
                StartSeconds = refConsensusStart,
                EndSeconds   = refConsensusStart + consensusLength
            };

            // Matched episodes
            foreach (var m in matches)
            {
                result[m.EpisodeId] = new IntroTimestamps
                {
                    StartSeconds = m.EpisodeStart,
                    EndSeconds   = m.EpisodeStart + consensusLength
                };
            }

            // ── Phase 5: retry unmatched episodes ─────────────────────────────
            // We now know roughly where the intro should be. Use that as a prior
            // and retry with a relaxed threshold, accepting ONLY if the found start
            // is consistent with the consensus.
            if (unmatched.Count > 0)
            {
                int relaxedThreshold = Math.Min(_hammingThreshold + 6, 20);
                _logger.Info("StrmCompanion: retrying {0} unmatched episode(s) (threshold={1})",
                    unmatched.Count, relaxedThreshold);

                foreach (var ep in unmatched)
                {
                    var m = TryMatchNear(reference, ep, sps, relaxedThreshold, refConsensusStart);
                    if (m == null)
                    {
                        _logger.Info("StrmCompanion: '{0}' – no match even after retry", ep.EpisodeName);
                        continue;
                    }

                    // Validate start consistency against consensus
                    // (use the expected episode-start by assuming same offset as consensus reference start)
                    double expectedEpStart = m.EpisodeStart; // we only have the reference to compare
                    // Instead, verify ref-start is consistent with the consensus ref-start
                    if (Math.Abs(m.ReferenceStart - refConsensusStart) > MaxStartDeviation)
                    {
                        _logger.Info(
                            "StrmCompanion: '{0}' retry result rejected – ref start {1:F1}s is {2:F1}s " +
                            "from consensus {3:F1}s (outlier)",
                            ep.EpisodeName, m.ReferenceStart,
                            Math.Abs(m.ReferenceStart - refConsensusStart), refConsensusStart);
                        continue;
                    }

                    _logger.Info("StrmCompanion: '{0}' retry succeeded – start={1:F1}s",
                        ep.EpisodeName, m.EpisodeStart);

                    result[ep.EpisodeInternalId] = new IntroTimestamps
                    {
                        StartSeconds = m.EpisodeStart,
                        EndSeconds   = m.EpisodeStart + consensusLength
                    };
                }
            }

            return result;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Full comparison: alignment score check + run detection.</summary>
        private EpisodeMatch TryMatch(
            FingerprintData reference, FingerprintData other,
            double sps, int hammingThreshold)
        {
            var a = reference.Fingerprint;
            var b = other.Fingerprint;
            int minSamples = (int)(_minimumIntroSeconds * sps);
            if (a.Count < minSamples || b.Count < minSamples) return null;

            int windowSize   = minSamples * 2;
            int bestOffset   = FindBestOffset(a, b, windowSize, out double bestScore);

            if (bestScore < MinAlignmentScore)
            {
                _logger.Debug("StrmCompanion: '{0}' alignment score {1:P0} < {2:P0} – rejected",
                    other.EpisodeName, bestScore, MinAlignmentScore);
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

            for (int offset = lo; offset <= hi; offset++)
            {
                int aS = Math.Max(0, offset);
                int bS = Math.Max(0, -offset);
                int av = Math.Min(a.Count - aS, b.Count - bS);
                if (av < windowSize) continue;
                double s = SimilarityScore(a, b, aS, bS, windowSize);
                if (s > bestScore) { bestScore = s; bestOffset = offset; }
            }

            // Relax the score threshold slightly for retries
            if (bestScore < MinAlignmentScore - 0.10) return null;

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

        // ── Gap-tolerant run detection ────────────────────────────────────────

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

        // ── Offset search ─────────────────────────────────────────────────────

        private int FindBestOffset(List<uint> a, List<uint> b, int windowSize, out double bestScore)
        {
            int bestOffset = 0;
            bestScore = -1;
            int maxSlide = Math.Max(a.Count, b.Count) - windowSize;
            if (maxSlide < 0) maxSlide = 0;

            for (int offset = -maxSlide; offset <= maxSlide; offset++)
            {
                int aS = Math.Max(0, offset);
                int bS = Math.Max(0, -offset);
                int av = Math.Min(a.Count - aS, b.Count - bS);
                if (av < windowSize) continue;
                double s = SimilarityScore(a, b, aS, bS, windowSize);
                if (s > bestScore) { bestScore = s; bestOffset = offset; }
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
