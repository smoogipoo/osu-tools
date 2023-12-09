// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Catch.Tests;
using osu.Game.Rulesets.Mania.Tests;
using osu.Game.Rulesets.Osu.Tests;
using osu.Game.Rulesets.Taiko.Tests;

namespace PerformanceCalculator
{
    [Command("conversion-test")]
    public class ConversionTestCommand
    {
        private const string beatmaps_dir = "/home/smgi/Repos/diffcalc-sheet-generator/beatmaps";
        private const string mappings_dir = "/home/smgi/Repos/diffcalc-sheet-generator/ConversionMappings";

        public int OnExecute(CommandLineApplication app, IConsole console)
        {
            foreach (var b in Directory.GetFiles(beatmaps_dir))
            {
                var workingBeatmap = new ProcessorWorkingBeatmap(b);
                string filename = Path.GetFileNameWithoutExtension(b);

                switch (workingBeatmap.BeatmapInfo.Ruleset.OnlineID)
                {
                    case 0:
                        doComparison(0, workingBeatmap, filename);
                        doComparison(1, workingBeatmap, filename);
                        doComparison(2, workingBeatmap, filename);
                        doComparison(3, workingBeatmap, filename);
                        break;

                    case 1:
                        doComparison(1, workingBeatmap, filename);
                        break;

                    case 2:
                        doComparison(2, workingBeatmap, filename);
                        break;

                    case 3:
                        doComparison(3, workingBeatmap, filename);
                        break;
                }
            }

            return 0;
        }

        private void doComparison(int rulesetId, WorkingBeatmap workingBeatmap, string filename)
        {
            string suffix = rulesetId switch
            {
                0 => "Osu",
                1 => "Taiko",
                2 => "CatchTheBeat",
                3 => "OsuMania",
                _ => throw new ArgumentOutOfRangeException(nameof(rulesetId), rulesetId, null)
            };

            string mappingsFileName = Path.Combine(mappings_dir, suffix, $"{filename}-expected-conversion.json");

            if (!File.Exists(mappingsFileName))
                return;

            try
            {
                switch (rulesetId)
                {
                    case 0:
                        new OsuBeatmapConversionTest().Test(workingBeatmap, Array.Empty<Type>(), File.ReadAllText(mappingsFileName));
                        break;

                    case 1:
                        new TaikoBeatmapConversionTest().Test(workingBeatmap, Array.Empty<Type>(), File.ReadAllText(mappingsFileName));
                        break;

                    case 2:
                        new CatchBeatmapConversionTest().Test(workingBeatmap, Array.Empty<Type>(), File.ReadAllText(mappingsFileName));
                        break;

                    case 3:
                        new ManiaBeatmapConversionTest().Test(workingBeatmap, Array.Empty<Type>(), File.ReadAllText(mappingsFileName));
                        break;
                }
            }
            catch
            {
                Console.WriteLine($"FAIL: {filename},{rulesetId}");
            }
        }
    }
}
