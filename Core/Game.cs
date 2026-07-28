using System;
using System.Collections.Generic;

namespace Core
{
    public class Game
    {
        public enum GameType
        {
            hk4e_global, hk4e_cn
        }

        private sealed record GameInfo(
            Region Region,
            string GameId,
            string LauncherId,
            string PackageId,
            string PlatApp,
            string MainPassword,
            string PreDownloadPassword);

        private static readonly Dictionary<GameType, GameInfo> GameMap = new()
        {
            [GameType.hk4e_global] = new(
                Region.OSREL,
                "gopR6Cufr3",
                "VYTpXlbWo8",
                "ScSYQBFhu9",
                "ddxf6vlr1reo",
                "bDL4JUHL625x",
                "ZOJpUiKu4Sme"),

            [GameType.hk4e_cn] = new(
                Region.CNREL,
                "1Z8W5NHUQb",
                "jGHBHlcOq1",
                "8xfMve0uwQ",
                "ddxf5qt290cg",
                "CW8GbLNU8f",
                "EPq5oNru9q")
        };

        private readonly GameInfo _info;

        public GameType Type { get; }
        public Region Region => _info.Region;
        public string GameId => _info.GameId;
        public string LauncherId => _info.LauncherId;
        public string PackageId => _info.PackageId;
        public string PlatApp => _info.PlatApp;

        public Game(string id)
        {
            if (!Enum.TryParse(id, true, out GameType game) ||
                !GameMap.TryGetValue(game, out var info))
            {
                throw new ArgumentException($"Unsupported game '{id}'.");
            }

            Type = game;
            _info = info;
        }

        public string GetPassword(BranchType branch) => branch switch
        {
            BranchType.Main => _info.MainPassword,
            BranchType.PreDownload => _info.PreDownloadPassword,
            _ => throw new ArgumentOutOfRangeException(nameof(branch))
        };
    }
}
