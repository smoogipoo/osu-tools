// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Timing;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch.Objects;
using osu.Game.Rulesets.Catch.Scoring;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mania.Scoring;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;

namespace PerformanceCalculator
{
    [Command("health")]
    public partial class RunHealthProcessorCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Name = "path", Description = "Required. A beatmap file (.osu) or ID to compute the difficulty for.")]
        public string Beatmap { get; }

        [UsedImplicitly]
        [Option(CommandOptionType.NoValue)]
        public bool Log { get; }

        [UsedImplicitly]
        [Option(CommandOptionType.NoValue)]
        public bool NoComboEndBonus { get; }

        [UsedImplicitly]
        [Option(CommandOptionType.SingleValue)]
        public int Ruleset { get; }

        public void OnExecute(CommandLineApplication app, IConsole console)
        {
            Console.WriteLine("beatmap\tv1_drain(s)\tv2_drain(s)\tchange(%)");

            if (Directory.Exists(Beatmap))
            {
                foreach (var f in Directory.GetFiles(Beatmap))
                    processSingle(f);
            }
            else
                processSingle(Beatmap);
        }

        private void processSingle(string fileOrId)
        {
            WorkingBeatmap beatmap = ProcessorWorkingBeatmap.FromFileOrId(fileOrId);
            Ruleset ruleset = LegacyHelper.GetRulesetFromLegacyID(Ruleset);

            // Only process beatmaps that can be converted to the requested ruleset.
            if (beatmap.BeatmapInfo.Ruleset.OnlineID != 0 && beatmap.BeatmapInfo.Ruleset.OnlineID != ruleset.RulesetInfo.OnlineID)
                return;

            IBeatmap playableBeatmap = beatmap.GetPlayableBeatmap(ruleset.RulesetInfo, new[] { new OsuModClassic() });

            DrainingHealthProcessor legacyProcessor;
            DrainingHealthProcessor newProcessor;

            switch (Ruleset)
            {
                case 0:
                    legacyProcessor = new LegacyOsuHealthProcessor(playableBeatmap.HitObjects[0].StartTime)
                    {
                        OnIterationFail = onIterationFail,
                        OnIterationSuccess = onIterationSuccess,
                        ApplyComboEndBonus = !NoComboEndBonus
                    };

                    newProcessor = new OsuHealthProcessor(playableBeatmap.HitObjects[0].StartTime)
                    {
                        OnIterationFail = onIterationFail,
                        OnIterationSuccess = onIterationSuccess
                    };

                    break;

                case 2:
                    legacyProcessor = new LegacyCatchHealthProcessor(playableBeatmap.HitObjects[0].StartTime)
                    {
                        OnIterationFail = onIterationFail,
                        OnIterationSuccess = onIterationSuccess,
                        ApplyComboEndBonus = !NoComboEndBonus
                    };

                    newProcessor = new CatchHealthProcessor(playableBeatmap.HitObjects[0].StartTime)
                    {
                        OnIterationFail = onIterationFail,
                        OnIterationSuccess = onIterationSuccess
                    };

                    break;

                case 3:
                    legacyProcessor = new LegacyManiaHealthProcessor(playableBeatmap.HitObjects[0].StartTime)
                    {
                        OnIterationFail = onIterationFail,
                        OnIterationSuccess = onIterationSuccess,
                    };

                    newProcessor = new ManiaHealthProcessor(playableBeatmap.HitObjects[0].StartTime)
                    {
                        OnIterationFail = onIterationFail,
                        OnIterationSuccess = onIterationSuccess
                    };

                    break;

                default:
                    throw new NotImplementedException();
            }

            if (Log)
                Console.WriteLine("Testing legacy processor...");
            legacyProcessor.ApplyBeatmap(playableBeatmap);

            if (Log)
                Console.WriteLine("Testing new processor...");
            newProcessor.ApplyBeatmap(playableBeatmap);

            double drainV1 = legacyProcessor.DrainRate;
            double drainV2 = newProcessor.DrainRate;
            double ratio = drainV1 == 0 ? 0 : (drainV2 - drainV1) / drainV1;

            Console.WriteLine($"{fileOrId.Substring(fileOrId.LastIndexOf('/') + 1)}\t{drainV1}\t{drainV2}\t{ratio:P}");
        }

        private void onIterationFail(string message)
        {
            if (Log)
                Console.WriteLine(message);
        }

        private void onIterationSuccess(string message)
        {
            if (Log)
                Console.WriteLine(message);
        }

        #region osu

        /// <summary>
        /// Reference implementation for osu!stable's HP drain.
        /// Cannot be used for gameplay.
        /// </summary>
        public partial class LegacyOsuHealthProcessor : DrainingHealthProcessor
        {
            private const double hp_bar_maximum = 200;
            private const double hp_combo_geki = 14;
            private const double hp_hit_300 = 6;
            private const double hp_slider_repeat = 4;
            private const double hp_slider_tick = 3;

            public Action<string> OnIterationFail;
            public Action<string> OnIterationSuccess;
            public bool ApplyComboEndBonus { get; set; } = true;

            private double lowestHpEver;
            private double lowestHpEnd;
            private double lowestHpComboEnd;
            private double hpRecoveryAvailable;
            private double hpMultiplierNormal;
            private double hpMultiplierComboEnd;

            public LegacyOsuHealthProcessor(double drainStartTime)
                : base(drainStartTime)
            {
            }

            public override void ApplyBeatmap(IBeatmap beatmap)
            {
                lowestHpEver = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 195, 160, 60);
                lowestHpComboEnd = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 198, 170, 80);
                lowestHpEnd = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 198, 180, 80);
                hpRecoveryAvailable = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 8, 4, 0);

                base.ApplyBeatmap(beatmap);
            }

            protected override void ApplyResultInternal(JudgementResult result)
            {
                if (!IsSimulating)
                    throw new NotSupportedException("The legacy osu! health processor is not supported for gameplay.");
            }

            protected override void RevertResultInternal(JudgementResult result)
            {
                if (!IsSimulating)
                    throw new NotSupportedException("The legacy osu! health processor is not supported for gameplay.");
            }

            protected override void Reset(bool storeResults)
            {
                hpMultiplierNormal = 1;
                hpMultiplierComboEnd = 1;

                base.Reset(storeResults);
            }

            protected override double ComputeDrainRate()
            {
                double testDrop = 0.05;
                double currentHp;
                double currentHpUncapped;

                do
                {
                    currentHp = hp_bar_maximum;
                    currentHpUncapped = hp_bar_maximum;

                    double lowestHp = currentHp;
                    double lastTime = DrainStartTime;
                    int currentBreak = 0;
                    bool fail = false;
                    int comboTooLowCount = 0;
                    string failReason = string.Empty;

                    for (int i = 0; i < Beatmap.HitObjects.Count; i++)
                    {
                        HitObject h = Beatmap.HitObjects[i];

                        // Find active break (between current and lastTime)
                        double localLastTime = lastTime;
                        double breakTime = 0;

                        // Subtract any break time from the duration since the last object
                        if (Beatmap.Breaks.Count > 0 && currentBreak < Beatmap.Breaks.Count)
                        {
                            BreakPeriod e = Beatmap.Breaks[currentBreak];

                            if (e.StartTime >= localLastTime && e.EndTime <= h.StartTime)
                            {
                                // consider break start equal to object end time for version 8+ since drain stops during this time
                                breakTime = (Beatmap.BeatmapInfo.BeatmapVersion < 8) ? (e.EndTime - e.StartTime) : e.EndTime - localLastTime;
                                currentBreak++;
                            }
                        }

                        reduceHp(testDrop * (h.StartTime - lastTime - breakTime));

                        lastTime = h.GetEndTime();

                        if (currentHp < lowestHp)
                            lowestHp = currentHp;

                        if (currentHp <= lowestHpEver)
                        {
                            fail = true;
                            testDrop *= 0.96;
                            failReason = $"hp too low ({currentHp / hp_bar_maximum} < {lowestHpEver / hp_bar_maximum})";
                            break;
                        }

                        double hpReduction = testDrop * (h.GetEndTime() - h.StartTime);
                        double hpOverkill = Math.Max(0, hpReduction - currentHp);
                        reduceHp(hpReduction);

                        if (h is Slider slider)
                        {
                            for (int j = 0; j < slider.RepeatCount + 2; j++)
                                increaseHp(hpMultiplierNormal * hp_slider_repeat);
                            foreach (var _ in slider.NestedHitObjects.OfType<SliderTick>())
                                increaseHp(hpMultiplierNormal * hp_slider_tick);
                        }
                        else if (h is Spinner spinner)
                        {
                            foreach (var _ in spinner.NestedHitObjects.Where(t => t is not SpinnerBonusTick))
                                increaseHp(hpMultiplierNormal * 1.7);
                        }

                        if (hpOverkill > 0 && currentHp - hpOverkill <= lowestHpEver)
                        {
                            fail = true;
                            testDrop *= 0.96;
                            failReason = $"overkill ({currentHp / hp_bar_maximum} - {hpOverkill / hp_bar_maximum} <= {lowestHpEver / hp_bar_maximum})";
                            break;
                        }

                        if (ApplyComboEndBonus && (i == Beatmap.HitObjects.Count - 1 || ((OsuHitObject)Beatmap.HitObjects[i + 1]).NewCombo))
                        {
                            increaseHp(hpMultiplierComboEnd * hp_combo_geki + hpMultiplierNormal * hp_hit_300);

                            if (currentHp < lowestHpComboEnd)
                            {
                                if (++comboTooLowCount > 2)
                                {
                                    hpMultiplierComboEnd *= 1.07;
                                    hpMultiplierNormal *= 1.03;
                                    fail = true;
                                    failReason = $"combo end hp too low ({currentHp / hp_bar_maximum} < {lowestHpComboEnd / hp_bar_maximum})";
                                    break;
                                }
                            }
                        }
                        else
                            increaseHp(hpMultiplierNormal * hp_hit_300);
                    }

                    if (!fail && currentHp < lowestHpEnd)
                    {
                        fail = true;
                        testDrop *= 0.94;
                        hpMultiplierComboEnd *= 1.01;
                        hpMultiplierNormal *= 1.01;
                        failReason = $"end hp too low ({currentHp / hp_bar_maximum} < {lowestHpEnd / hp_bar_maximum})";
                    }

                    double recovery = (currentHpUncapped - hp_bar_maximum) / Beatmap.HitObjects.Count;

                    if (!fail && recovery < hpRecoveryAvailable)
                    {
                        fail = true;
                        testDrop *= 0.96;
                        hpMultiplierComboEnd *= 1.02;
                        hpMultiplierNormal *= 1.01;
                        failReason = $"recovery too low ({recovery / hp_bar_maximum} < {hpRecoveryAvailable / hp_bar_maximum})";
                    }

                    if (fail)
                    {
                        OnIterationFail?.Invoke($"FAILED drop {testDrop / hp_bar_maximum}: {failReason}");
                        continue;
                    }

                    OnIterationSuccess?.Invoke($"PASSED drop {testDrop / hp_bar_maximum}");
                    return testDrop / hp_bar_maximum;
                } while (true);

                void reduceHp(double amount)
                {
                    currentHpUncapped = Math.Max(0, currentHpUncapped - amount);
                    currentHp = Math.Max(0, currentHp - amount);
                }

                void increaseHp(double amount)
                {
                    currentHpUncapped += amount;
                    currentHp = Math.Max(0, Math.Min(hp_bar_maximum, currentHp + amount));
                }
            }
        }

        #endregion

        #region catch

        /// <summary>
        /// Reference implementation for osu!stable's HP drain.
        /// Cannot be used for gameplay.
        /// </summary>
        public partial class LegacyCatchHealthProcessor : DrainingHealthProcessor
        {
            private const double hp_bar_maximum = 200;
            private const double hp_combo_geki = 14;
            private const double hp_hit_300 = 6;
            private const double hp_slider_tick = 3;

            public Action<string> OnIterationFail;
            public Action<string> OnIterationSuccess;
            public bool ApplyComboEndBonus { get; set; } = true;

            private double lowestHpEver;
            private double lowestHpEnd;
            private double lowestHpComboEnd;
            private double hpRecoveryAvailable;
            private double hpMultiplierNormal;
            private double hpMultiplierComboEnd;

            public LegacyCatchHealthProcessor(double drainStartTime)
                : base(drainStartTime)
            {
            }

            public override void ApplyBeatmap(IBeatmap beatmap)
            {
                lowestHpEver = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 195, 160, 60);
                lowestHpComboEnd = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 198, 170, 80);
                lowestHpEnd = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 198, 180, 80);
                hpRecoveryAvailable = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 8, 4, 0);

                base.ApplyBeatmap(beatmap);
            }

            protected override void ApplyResultInternal(JudgementResult result)
            {
                if (!IsSimulating)
                    throw new NotSupportedException("The legacy catch health processor is not supported for gameplay.");
            }

            protected override void RevertResultInternal(JudgementResult result)
            {
                if (!IsSimulating)
                    throw new NotSupportedException("The legacy catch health processor is not supported for gameplay.");
            }

            protected override void Reset(bool storeResults)
            {
                hpMultiplierNormal = 1;
                hpMultiplierComboEnd = 1;

                base.Reset(storeResults);
            }

            protected override double ComputeDrainRate()
            {
                double testDrop = 0.05;
                double currentHp;
                double currentHpUncapped;

                List<(HitObject hitObject, bool newCombo)> allObjects = enumerateHitObjects(Beatmap).Where(h => h.hitObject is Fruit || h.hitObject is Droplet || h.hitObject is Banana).ToList();

                do
                {
                    currentHp = hp_bar_maximum;
                    currentHpUncapped = hp_bar_maximum;

                    double lowestHp = currentHp;
                    double lastTime = DrainStartTime;
                    int currentBreak = 0;
                    bool fail = false;
                    int comboTooLowCount = 0;
                    string failReason = string.Empty;

                    for (int i = 0; i < allObjects.Count; i++)
                    {
                        HitObject h = allObjects[i].hitObject;

                        // Find active break (between current and lastTime)
                        double localLastTime = lastTime;
                        double breakTime = 0;

                        // Subtract any break time from the duration since the last object
                        if (Beatmap.Breaks.Count > 0 && currentBreak < Beatmap.Breaks.Count)
                        {
                            BreakPeriod e = Beatmap.Breaks[currentBreak];

                            if (e.StartTime >= localLastTime && e.EndTime <= h.StartTime)
                            {
                                // consider break start equal to object end time for version 8+ since drain stops during this time
                                breakTime = (Beatmap.BeatmapInfo.BeatmapVersion < 8) ? (e.EndTime - e.StartTime) : e.EndTime - localLastTime;
                                currentBreak++;
                            }
                        }

                        reduceHp(testDrop * (h.StartTime - lastTime - breakTime));

                        lastTime = h.GetEndTime();

                        if (currentHp < lowestHp)
                            lowestHp = currentHp;

                        if (currentHp <= lowestHpEver)
                        {
                            fail = true;
                            testDrop *= 0.96;
                            failReason = $"hp too low ({currentHp / hp_bar_maximum} < {lowestHpEver / hp_bar_maximum})";
                            break;
                        }

                        switch (h)
                        {
                            case Fruit:
                                if (ApplyComboEndBonus && (i == allObjects.Count - 1 || allObjects[i + 1].newCombo))
                                {
                                    increaseHp(hpMultiplierComboEnd * hp_combo_geki + hpMultiplierNormal * hp_hit_300);

                                    if (currentHp < lowestHpComboEnd)
                                    {
                                        if (++comboTooLowCount > 2)
                                        {
                                            hpMultiplierComboEnd *= 1.07;
                                            hpMultiplierNormal *= 1.03;
                                            fail = true;
                                            failReason = $"combo end hp too low ({currentHp / hp_bar_maximum} < {lowestHpComboEnd / hp_bar_maximum})";
                                        }
                                    }
                                }
                                else
                                    increaseHp(hpMultiplierNormal * hp_hit_300);

                                break;

                            case Banana:
                                increaseHp(hpMultiplierNormal / 2);
                                break;

                            case TinyDroplet:
                                increaseHp(hpMultiplierNormal * hp_slider_tick * 0.1);
                                break;

                            case Droplet:
                                increaseHp(hpMultiplierNormal * hp_slider_tick);
                                break;
                        }

                        if (fail)
                            break;
                    }

                    if (!fail && currentHp < lowestHpEnd)
                    {
                        fail = true;
                        testDrop *= 0.94;
                        hpMultiplierComboEnd *= 1.01;
                        hpMultiplierNormal *= 1.01;
                        failReason = $"end hp too low ({currentHp / hp_bar_maximum} < {lowestHpEnd / hp_bar_maximum})";
                    }

                    double recovery = (currentHpUncapped - hp_bar_maximum) / allObjects.Count;

                    if (!fail && recovery < hpRecoveryAvailable)
                    {
                        fail = true;
                        testDrop *= 0.96;
                        hpMultiplierComboEnd *= 1.02;
                        hpMultiplierNormal *= 1.01;
                        failReason = $"recovery too low ({recovery / hp_bar_maximum} < {hpRecoveryAvailable / hp_bar_maximum})";
                    }

                    if (fail)
                    {
                        OnIterationFail?.Invoke($"FAILED drop {testDrop / hp_bar_maximum}: {failReason}");
                        continue;
                    }

                    OnIterationSuccess?.Invoke($"PASSED drop {testDrop / hp_bar_maximum}");
                    return testDrop / hp_bar_maximum;
                } while (true);

                void reduceHp(double amount)
                {
                    currentHpUncapped = Math.Max(0, currentHpUncapped - amount);
                    currentHp = Math.Max(0, currentHp - amount);
                }

                void increaseHp(double amount)
                {
                    currentHpUncapped += amount;
                    currentHp = Math.Max(0, Math.Min(hp_bar_maximum, currentHp + amount));
                }
            }

            private IEnumerable<(HitObject hitObject, bool newCombo)> enumerateHitObjects(IBeatmap beatmap)
            {
                return enumerateRecursively(beatmap.HitObjects);

                static IEnumerable<(HitObject hitObject, bool newCombo)> enumerateRecursively(IEnumerable<HitObject> hitObjects)
                {
                    foreach (var hitObject in hitObjects)
                    {
                        // The combo end will either be attached to the hitobject itself if it has no children, or the very first child if it has children.
                        bool newCombo = (hitObject as IHasComboInformation)?.NewCombo ?? false;

                        foreach ((HitObject nested, bool _) in enumerateRecursively(hitObject.NestedHitObjects))
                        {
                            yield return (nested, newCombo);

                            // Since the combo was attached to the first child, don't attach it to any other child or the parenting hitobject itself.
                            newCombo = false;
                        }

                        yield return (hitObject, newCombo);
                    }
                }
            }
        }

        #endregion

        #region mania

        /// <summary>
        /// Reference implementation for osu!stable's HP drain.
        /// Cannot be used for gameplay.
        /// </summary>
        public partial class LegacyManiaHealthProcessor : DrainingHealthProcessor
        {
            private const double hp_bar_maximum = 200;

            public Action<string> OnIterationFail;
            public Action<string> OnIterationSuccess;

            private double lowestHpEver;
            private double lowestHpEnd;
            private double hpRecoveryAvailable;
            private double hpMultiplierNormal;

            public LegacyManiaHealthProcessor(double drainStartTime)
                : base(drainStartTime)
            {
            }

            public override void ApplyBeatmap(IBeatmap beatmap)
            {
                lowestHpEver = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 195, 160, 60);
                lowestHpEnd = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 198, 180, 80);
                hpRecoveryAvailable = IBeatmapDifficultyInfo.DifficultyRange(beatmap.Difficulty.DrainRate, 8, 4, 0);

                base.ApplyBeatmap(beatmap);
            }

            protected override void ApplyResultInternal(JudgementResult result)
            {
                if (!IsSimulating)
                    throw new NotSupportedException("The legacy osu! health processor is not supported for gameplay.");
            }

            protected override void RevertResultInternal(JudgementResult result)
            {
                if (!IsSimulating)
                    throw new NotSupportedException("The legacy osu! health processor is not supported for gameplay.");
            }

            protected override void Reset(bool storeResults)
            {
                hpMultiplierNormal = 1;

                base.Reset(storeResults);
            }

            protected override double ComputeDrainRate()
            {
                double testDrop = 0.05;
                double currentHp;
                double currentHpUncapped;

                do
                {
                    currentHp = hp_bar_maximum;
                    currentHpUncapped = hp_bar_maximum;

                    double lowestHp = currentHp;
                    double lastTime = DrainStartTime;
                    int currentBreak = 0;
                    bool fail = false;
                    string failReason = string.Empty;

                    for (int i = 0; i < Beatmap.HitObjects.Count; i++)
                    {
                        HitObject h = Beatmap.HitObjects[i];

                        // Find active break (between current and lastTime)
                        double localLastTime = lastTime;
                        double breakTime = 0;

                        // Subtract any break time from the duration since the last object
                        if (Beatmap.Breaks.Count > 0 && currentBreak < Beatmap.Breaks.Count)
                        {
                            BreakPeriod e = Beatmap.Breaks[currentBreak];

                            if (e.StartTime >= localLastTime && e.EndTime <= h.StartTime)
                            {
                                // consider break start equal to object end time for version 8+ since drain stops during this time
                                breakTime = (Beatmap.BeatmapInfo.BeatmapVersion < 8) ? (e.EndTime - e.StartTime) : e.EndTime - localLastTime;
                                currentBreak++;
                            }
                        }

                        reduceHp(testDrop * (h.StartTime - lastTime - breakTime));

                        lastTime = h.GetEndTime();

                        if (currentHp < lowestHp)
                            lowestHp = currentHp;

                        if (currentHp <= lowestHpEver)
                        {
                            fail = true;
                            testDrop *= 0.96;
                            failReason = $"hp too low ({currentHp / hp_bar_maximum} < {lowestHpEver / hp_bar_maximum})";
                            break;
                        }

                        double hpReduction = testDrop * (h.GetEndTime() - h.StartTime);
                        double hpOverkill = Math.Max(0, hpReduction - currentHp);
                        reduceHp(hpReduction);

                        if (h is HoldNote)
                            increaseHp(hpMultiplierNormal * (1.1 - Beatmap.Difficulty.DrainRate * 0.1));

                        if (hpOverkill > 0 && currentHp - hpOverkill <= lowestHpEver)
                        {
                            fail = true;
                            testDrop *= 0.96;
                            failReason = $"overkill ({currentHp / hp_bar_maximum} - {hpOverkill / hp_bar_maximum} <= {lowestHpEver / hp_bar_maximum})";
                            break;
                        }

                        increaseHp(hpMultiplierNormal * (1.1 - Beatmap.Difficulty.DrainRate * 0.1));
                    }

                    if (!fail && currentHp < lowestHpEnd)
                    {
                        fail = true;
                        testDrop *= 0.94;
                        hpMultiplierNormal *= 1.01;
                        failReason = $"end hp too low ({currentHp / hp_bar_maximum} < {lowestHpEnd / hp_bar_maximum})";
                    }

                    double recovery = (currentHpUncapped - hp_bar_maximum) / Beatmap.HitObjects.Count;

                    if (!fail && recovery < hpRecoveryAvailable)
                    {
                        fail = true;
                        testDrop *= 0.96;
                        hpMultiplierNormal *= 1.01;
                        failReason = $"recovery too low ({recovery / hp_bar_maximum} < {hpRecoveryAvailable / hp_bar_maximum})";
                    }

                    if (fail)
                    {
                        OnIterationFail?.Invoke($"FAILED drop {testDrop / hp_bar_maximum}: {failReason}");
                        continue;
                    }

                    OnIterationSuccess?.Invoke($"PASSED drop {testDrop / hp_bar_maximum}");
                    return testDrop / hp_bar_maximum;
                } while (true);

                void reduceHp(double amount)
                {
                    currentHpUncapped = Math.Max(0, currentHpUncapped - amount);
                    currentHp = Math.Max(0, currentHp - amount);
                }

                void increaseHp(double amount)
                {
                    currentHpUncapped += amount;
                    currentHp = Math.Max(0, Math.Min(hp_bar_maximum, currentHp + amount));
                }
            }
        }

        #endregion
    }
}
