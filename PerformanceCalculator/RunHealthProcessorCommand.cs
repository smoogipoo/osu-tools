// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Scoring;

namespace PerformanceCalculator
{
    [Command("health")]
    public class RunHealthProcessorCommand
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
            OsuRuleset ruleset = new OsuRuleset();

            if (!beatmap.BeatmapInfo.Ruleset.Equals(ruleset.RulesetInfo))
                return;

            IBeatmap playableBeatmap = beatmap.GetPlayableBeatmap(ruleset.RulesetInfo, new[] { new OsuModClassic() });

            LegacyOsuHealthProcessor legacyProcessor = new LegacyOsuHealthProcessor(playableBeatmap.HitObjects[0].StartTime)
            {
                OnIterationFail = onIterationFail,
                OnIterationSuccess = onIterationSuccess,
                ApplyComboEndBonus = !NoComboEndBonus
            };

            OsuHealthProcessor newProcessor = new OsuHealthProcessor(playableBeatmap.HitObjects[0].StartTime)
            {
                OnIterationFail = onIterationFail,
                OnIterationSuccess = onIterationSuccess
            };

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
    }
}
