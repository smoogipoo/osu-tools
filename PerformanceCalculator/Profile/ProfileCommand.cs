// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Alba.CsConsoleFormat;
using JetBrains.Annotations;
using LibGit2Sharp;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using osu.Framework.IO.Network;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace PerformanceCalculator.Profile
{
    [Command(Name = "profile", Description = "Computes the total performance (pp) of a profile.")]
    public class ProfileCommand : ApiCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Name = "user", Description = "User ID is preferred, but username should also work.")]
        public string ProfileName { get; }

        [UsedImplicitly]
        [Option(Template = "-r|--ruleset:<ruleset-id>", Description = "The ruleset to compute the profile for.\n"
                                                                      + "Values: 0 - osu!, 1 - osu!taiko, 2 - osu!catch, 3 - osu!mania")]
        [AllowedValues("0", "1", "2", "3")]
        public int? Ruleset { get; }

        [UsedImplicitly]
        [Option(Template = "-j|--json", Description = "Output results as JSON.")]
        public bool OutputJson { get; }

        public override void Execute()
        {
            // Supported values:
            //   Repos:      https://github.com/ppy/osu
            //   PRs:        https://github.com/ppy/osu/pull/15845
            //   Trees:      https://github.com/ppy/osu/tree/34b0e374d855cacde830c91b7251f74d499d1a1d
            //   Commits:    https://github.com/ppy/osu/commit/34b0e374d855cacde830c91b7251f74d499d1a1d
            //   PR Commits: https://github.com/ppy/osu/pull/19120/commits/5f70ee3ed7b09042c737d99f67f326f3227b9776
            const string remote_repo = "https://github.com/ppy/osu/pull/19977";

            Match match = Regex.Match(remote_repo, @"^(https:\/\/github\.com\/[^\/]+\/[^\/]+)\/?([^\/]+)?\/?([^\/]+)?\/?(?:commits)?\/?(.*)?");

            if (!match.Success)
            {
                Console.WriteLine("Invalid repo URL.");
                return;
            }

            string repoUrl = match.Groups[1].Value;
            string fetchType = match.Groups.Count >= 3 ? match.Groups[2].Value : null;
            string commitSha = match.Groups.Count >= 4 ? match.Groups[3].Value : null;
            string pullRequestCommitSha = match.Groups.Count >= 5 ? match.Groups[4].Value : null;

            AssemblyLoadContext remoteAssemblyContext = new AssemblyLoadContext("osu-b");

            Console.WriteLine("Creating temporary directory...");
            var repoDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

            try
            {
                if (fetchType == "pull")
                {
                    var req = new WebRequest(remote_repo);
                    req.Perform();

                    Match tree = Regex.Match(req.GetResponseString(), @"href=""\/([^\/]+\/[^\/]+)\/tree\/([^""]+)\""");
                    Match commitId = Regex.Matches(req.GetResponseString(), @"\/pull\/\d+\/commits/([^""]+)""").Last();

                    if (!tree.Success)
                    {
                        Console.WriteLine("Could not determine the pull request's real tree.");
                        return;
                    }

                    if (!commitId.Success)
                    {
                        Console.WriteLine("Could not determine the pull request's last commit.");
                        return;
                    }

                    repoUrl = $"https://github.com/{tree.Groups[1].Value}";
                    commitSha = string.IsNullOrEmpty(pullRequestCommitSha) ? commitId.Groups[1].Value : pullRequestCommitSha;
                }

                Console.WriteLine($"Checking out repository \"{repoUrl}\" into {repoDir}...");
                Repository repo = new Repository(Repository.Clone(repoUrl, repoDir.FullName));

                if (!string.IsNullOrEmpty(commitSha))
                {
                    Console.WriteLine($"Checking out {commitSha}");
                    Commands.Checkout(repo, commitSha);
                }

                Console.WriteLine("Invoking dotnet build...");
                var process = Process.Start(new ProcessStartInfo("dotnet")
                {
                    ArgumentList =
                    {
                        "build",
                        "-c",
                        "Release",
                        Path.Combine(repoDir.FullName, "osu.Desktop.slnf")
                    },
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                });

                process!.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine("Failed to compile:");
                    Console.WriteLine(process.StandardOutput.ReadToEnd());
                    return;
                }

                Console.WriteLine("Compilation succeeded!");
                Console.WriteLine("Loading rulesets for B context...");

                using (remoteAssemblyContext.EnterContextualReflection())
                {
                    foreach (string path in Directory.GetFiles(Path.Combine(repoDir.FullName, "osu.Desktop", "bin", "Release", "net6.0"), "osu.Game.Rulesets.*.dll").Where(f => !f.Contains("Tests")))
                    {
                        remoteAssemblyContext.LoadFromAssemblyPath(path);
                        Console.WriteLine($"Loaded {path}!");
                    }
                }
            }
            finally
            {
                Console.WriteLine("Cleaning up...");

                try
                {
                    repoDir.Delete(true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to delete repository directory: {repoDir}");
                    Console.WriteLine(ex);
                }
            }

            var displayPlays = new List<UserPlayInfo>();

            var ruleset = LegacyHelper.GetRulesetFromLegacyID(Ruleset ?? 0);
            var rulesetApiName = LegacyHelper.GetRulesetShortNameFromId(Ruleset ?? 0);

            Console.WriteLine("Getting user data...");
            var userData = GetJsonFromApi<APIUser>($"users/{ProfileName}/{ruleset.ShortName}");

            Console.WriteLine("Getting user top scores...");

            foreach (var play in GetJsonFromApi<List<SoloScoreInfo>>($"users/{userData.Id}/scores/best?mode={rulesetApiName}&limit=100"))
            {
                (DifficultyAttributes difficulty, PerformanceAttributes performance) localAttributes;

                using (AssemblyLoadContext.Default.EnterContextualReflection())
                {
                    var localBeatmap = ProcessorWorkingBeatmap.FromFileOrId(play.BeatmapID.ToString());
                    var localScore = new ProcessorScoreDecoder(localBeatmap).Parse(play.ToScoreInfo(play.Mods.Select(x => x.ToMod(ruleset)).ToArray()));

                    localAttributes = compute(ruleset, localBeatmap, localScore.ScoreInfo);
                }

                (DifficultyAttributes difficulty, PerformanceAttributes performance) remoteAttributes;

                using (remoteAssemblyContext.EnterContextualReflection())
                {
                    var remoteRuleset = ruleset.RulesetInfo.CreateInstance();
                    var remoteBeatmap = ProcessorWorkingBeatmap.FromFileOrId(play.BeatmapID.ToString());
                    var remoteScore = new ProcessorScoreDecoder(remoteBeatmap).Parse(play.ToScoreInfo(play.Mods.Select(x => x.ToMod(remoteRuleset)).ToArray()));

                    for (int i = 0; i < remoteScore.ScoreInfo.Mods.Length; i++)
                        remoteScore.ScoreInfo.Mods[i] = (Mod)Activator.CreateInstance(Type.GetType(remoteScore.ScoreInfo.Mods[i]!.GetType().AssemblyQualifiedName));

                    remoteAttributes = compute(remoteRuleset, remoteBeatmap, remoteScore.ScoreInfo);
                }

                var working = ProcessorWorkingBeatmap.FromFileOrId(play.BeatmapID.ToString());
                var score = new ProcessorScoreDecoder(working).Parse(play.ToScoreInfo(play.Mods.Select(x => x.ToMod(ruleset)).ToArray()));

                var thisPlay = new UserPlayInfo
                {
                    Beatmap = working.BeatmapInfo,
                    LocalPP = localAttributes.performance?.Total ?? 0,
                    RemotePp = remoteAttributes.performance?.Total ?? 0,
                    Mods = score.ScoreInfo.Mods.Select(m => m.Acronym).ToArray(),
                    MissCount = play.Statistics.GetValueOrDefault(HitResult.Miss),
                    Accuracy = score.ScoreInfo.Accuracy * 100,
                    Combo = play.MaxCombo,
                    MaxCombo = localAttributes.difficulty.MaxCombo
                };

                displayPlays.Add(thisPlay);
            }

            var localOrdered = displayPlays.OrderByDescending(p => p.LocalPP).ToList();
            var liveOrdered = displayPlays.OrderByDescending(p => p.RemotePp).ToList();

            int index = 0;
            double totalLocalPP = localOrdered.Sum(play => Math.Pow(0.95, index++) * play.LocalPP);
            double totalLivePP = (double)(userData.Statistics.PP ?? 0);

            index = 0;
            double nonBonusLivePP = liveOrdered.Sum(play => Math.Pow(0.95, index++) * play.RemotePp);

            //todo: implement properly. this is pretty damn wrong.
            var playcountBonusPP = (totalLivePP - nonBonusLivePP);
            totalLocalPP += playcountBonusPP;

            double totalDiffPP = totalLocalPP - totalLivePP;

            if (OutputJson)
            {
                var json = JsonConvert.SerializeObject(new
                {
                    Username = userData.Username,
                    LivePp = totalLivePP,
                    LocalPp = totalLocalPP,
                    PlaycountPp = playcountBonusPP,
                    Scores = localOrdered.Select(item => new
                    {
                        BeatmapId = item.Beatmap.OnlineID,
                        BeatmapName = item.Beatmap.ToString(),
                        item.Combo,
                        item.Accuracy,
                        item.MissCount,
                        item.Mods,
                        RemotePp = item.RemotePp,
                        LocalPp = item.LocalPP,
                        PositionChange = liveOrdered.IndexOf(item) - localOrdered.IndexOf(item)
                    })
                });

                Console.Write(json);

                if (OutputFile != null)
                    File.WriteAllText(OutputFile, json);
            }

            else
            {
                OutputDocument(new Document(
                    new Span($"User:     {userData.Username}"), "\n",
                    new Span($"Remote PP:  {totalLivePP:F1} (including {playcountBonusPP:F1}pp from playcount)"), "\n",
                    new Span($"Local PP: {totalLocalPP:F1} ({totalDiffPP:+0.0;-0.0;-})"), "\n",
                    new Grid
                    {
                        Columns =
                        {
                            GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto
                        },
                        Children =
                        {
                            new Cell("#"),
                            new Cell("beatmap"),
                            new Cell("max combo"),
                            new Cell("accuracy"),
                            new Cell("misses"),
                            new Cell("mods"),
                            new Cell("remote pp"),
                            new Cell("local pp"),
                            new Cell("pp change"),
                            new Cell("position change"),
                            localOrdered.Select(item => new[]
                            {
                                new Cell($"{localOrdered.IndexOf(item) + 1}"),
                                new Cell($"{item.Beatmap.OnlineID} - {item.Beatmap}"),
                                new Cell($"{item.Combo}/{item.MaxCombo}x") { Align = Align.Right },
                                new Cell($"{Math.Round(item.Accuracy, 2)}%") { Align = Align.Right },
                                new Cell($"{item.MissCount}") { Align = Align.Right },
                                new Cell($"{(item.Mods.Length > 0 ? string.Join(", ", item.Mods) : "None")}") { Align = Align.Right },
                                new Cell($"{item.RemotePp:F1}") { Align = Align.Right },
                                new Cell($"{item.LocalPP:F1}") { Align = Align.Right },
                                new Cell($"{item.LocalPP - item.RemotePp:F1}") { Align = Align.Right },
                                new Cell($"{liveOrdered.IndexOf(item) - localOrdered.IndexOf(item):+0;-0;-}") { Align = Align.Center },
                            })
                        }
                    })
                );
            }
        }

        private (DifficultyAttributes difficulty, PerformanceAttributes performance) compute(Ruleset ruleset, WorkingBeatmap beatmap, ScoreInfo scoreInfo)
        {
            var difficultyCalculator = ruleset.CreateDifficultyCalculator(beatmap);
            var difficultyAttributes = difficultyCalculator.Calculate(LegacyHelper.ConvertToLegacyDifficultyAdjustmentMods(ruleset, scoreInfo.Mods).ToArray());
            var performanceCalculator = ruleset.CreatePerformanceCalculator();
            var ppAttributes = performanceCalculator?.Calculate(scoreInfo, difficultyAttributes);

            return (difficultyAttributes, ppAttributes);
        }
    }
}
