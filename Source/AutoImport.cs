using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Playnite.SDK.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AutoImportPlugin
{
    public class AutoImport : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private AutoImportSettingsViewModel settings { get; set; }

        private Process launcherProcess;
        private System.Threading.Timer launcherCheckTimer;

        private const string ReShadeDeployerPath =
            @"G:\Reshade\Reshade Deployer.exe";

        // ============================================================
        // DS4WINDOWS
        // ============================================================

        // Installation DS4Windows utilisée uniquement par Hugo.
        private const string Ds4WindowsUserName = "Hugo";

        private const string Ds4WindowsExePath =
            @"C:\Program Files (x86)\net8.0-windows7.0.Full\DS4Windows.exe";

        // Auto Profiles.xml se trouve dans le même dossier que DS4Windows.exe.
        private static string Ds4WindowsAutoProfilesPath
        {
            get
            {
                string directory =
                    Path.GetDirectoryName(Ds4WindowsExePath);

                return Path.Combine(
                    directory,
                    "Auto Profiles.xml"
                );
            }
        }

        // Controller choisi dans la fenêtre :
        // PS4 / XBOX / OFF
        private string pendingControllerSelection;

        // ============================================================
        // HDR
        // ============================================================

        // Chemin de l'exécutable du jeu pour lequel le HDR doit être activé.
        private string pendingHdrExecutablePath;

        // ============================================================
        // METADATA
        // ============================================================

        // Chemin de l'exécutable du jeu dont on attend les métadonnées.
        private string pendingMetadataExecutablePath;

        // Jeux pour lesquels le lien PCGamingWiki a déjà été ouvert.
        private readonly HashSet<Guid> openedPcGamingWikiGames =
            new HashSet<Guid>();

        public override Guid Id { get; } =
            Guid.Parse("92c96e54-069b-4fc4-bbaa-35ac3064f85a");

        public override string Name => "AutoImport";

        public AutoImport(IPlayniteAPI api) : base(api)
        {
            settings = new AutoImportSettingsViewModel(this);

            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };

            launcherCheckTimer = new System.Threading.Timer(
                CheckLauncherProcess,
                null,
                0,
                1000
            );
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override System.Windows.Controls.UserControl GetSettingsView(
            bool firstRunSettings)
        {
            return new AutoImportSettingsView();
        }

        public override IEnumerable<GameMetadata> GetGames(
            LibraryGetGamesArgs args)
        {
            return ScanAndSelectGames();
        }

        // ============================================================
        // SURVEILLANCE DU LAUNCHER
        // ============================================================

        private void CheckLauncherProcess(object state)
        {
            try
            {
                // Si on surveille déjà un launcher,
                // vérifier s'il est encore actif.
                if (launcherProcess != null)
                {
                    if (!launcherProcess.HasExited)
                        return;

                    launcherProcess.Dispose();
                    launcherProcess = null;

                    // Le launcher vient de se fermer :
                    // déclencher automatiquement le scan.
                    Application.Current.Dispatcher.BeginInvoke(
                        new Action(() =>
                        {
                            ScanAndSelectGames();
                        })
                    );

                    return;
                }

                // ========================================================
                // CHANGE "launcher" ICI SI TON EXE A UN AUTRE NOM
                // ========================================================

                var processes =
                    Process.GetProcessesByName("launcher");

                if (processes.Length > 0)
                {
                    launcherProcess = processes[0];
                }

                foreach (var process in processes.Skip(1))
                {
                    process.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger.Warn(
                    ex,
                    "Failed to monitor launcher process"
                );
            }
        }

        // ============================================================
        // LIBRARY UPDATED
        // ============================================================

        public override void OnLibraryUpdated(
            OnLibraryUpdatedEventArgs args)
        {
            base.OnLibraryUpdated(args);

            try
            {
                // ========================================================
                // HDR
                // ========================================================

                if (!string.IsNullOrEmpty(
                    pendingHdrExecutablePath))
                {
                    string targetPath =
                        NormalizePath(
                            pendingHdrExecutablePath
                        );

                    foreach (var game in PlayniteApi.Database.Games)
                    {
                        if (game.GameActions == null)
                            continue;

                        bool isTargetGame =
                            game.GameActions.Any(action =>
                                action.Type == GameActionType.File &&
                                !string.IsNullOrEmpty(action.Path) &&
                                NormalizePath(action.Path) ==
                                    targetPath
                            );

                        if (!isTargetGame)
                            continue;

                        game.EnableSystemHdr = true;

                        PlayniteApi.Database.Games.Update(game);

                        logger.Info(
                            $"Enabled system HDR for imported game: {game.Name}"
                        );

                        pendingHdrExecutablePath = null;

                        break;
                    }
                }

                // ========================================================
                // PCGAMINGWIKI
                // ========================================================

                if (!string.IsNullOrEmpty(
                    pendingMetadataExecutablePath))
                {
                    string targetPath =
                        NormalizePath(
                            pendingMetadataExecutablePath
                        );

                    foreach (var game in PlayniteApi.Database.Games)
                    {
                        if (game.GameActions == null)
                            continue;

                        bool isTargetGame =
                            game.GameActions.Any(action =>
                                action.Type == GameActionType.File &&
                                !string.IsNullOrEmpty(action.Path) &&
                                NormalizePath(action.Path) ==
                                    targetPath
                            );

                        if (!isTargetGame)
                            continue;

                        // Si le lien a déjà été ouvert pour ce jeu,
                        // on ne fait plus rien.
                        if (openedPcGamingWikiGames.Contains(game.Id))
                        {
                            pendingMetadataExecutablePath = null;
                            break;
                        }

                        // Cherche uniquement le lien dont le nom est
                        // "PCGamingWiki".
                        var pcGamingWikiLink =
                            game.Links?
                                .FirstOrDefault(link =>
                                    string.Equals(
                                        link.Name,
                                        "PCGamingWiki",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                );

                        // Le lien n'est pas encore arrivé.
                        if (pcGamingWikiLink == null)
                        {
                            logger.Info(
                                $"PCGamingWiki link not available yet for: {game.Name}"
                            );

                            break;
                        }

                        if (string.IsNullOrWhiteSpace(
                            pcGamingWikiLink.Url))
                        {
                            logger.Warn(
                                $"PCGamingWiki link has no URL for: {game.Name}"
                            );

                            break;
                        }

                        logger.Info(
                            $"PCGamingWiki link found for {game.Name}: {pcGamingWikiLink.Url}"
                        );

                        openedPcGamingWikiGames.Add(game.Id);

                        try
                        {
                            Process.Start(
                                new ProcessStartInfo
                                {
                                    FileName =
                                        pcGamingWikiLink.Url,

                                    UseShellExecute = true
                                }
                            );

                            logger.Info(
                                $"Opened PCGamingWiki page for: {game.Name}"
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.Error(
                                ex,
                                $"Failed to open PCGamingWiki for: {game.Name}"
                            );
                        }

                        pendingMetadataExecutablePath = null;

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed during library update processing"
                );
            }
        }

        // ============================================================
        // CLEANUP
        // ============================================================

        ~AutoImport()
        {
            try
            {
                launcherCheckTimer?.Dispose();
                launcherProcess?.Dispose();
            }
            catch
            {
            }
        }

        // ============================================================
        // EXISTING GAMES
        // ============================================================

        private HashSet<string> BuildExistingGamesSet()
        {
            var existingSet =
                new HashSet<string>();

            try
            {
                foreach (var game in PlayniteApi.Database.Games)
                {
                    if (game.IsInstalled)
                    {
                        if (!string.IsNullOrEmpty(
                            game.InstallDirectory))
                        {
                            existingSet.Add(
                                NormalizePath(
                                    game.InstallDirectory
                                )
                            );
                        }

                        if (game.GameActions != null)
                        {
                            foreach (var action in game.GameActions)
                            {
                                if (action.Type ==
                                        GameActionType.File &&
                                    !string.IsNullOrEmpty(
                                        action.Path))
                                {
                                    existingSet.Add(
                                        NormalizePath(
                                            action.Path
                                        )
                                    );
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to build existing games set"
                );

                return new HashSet<string>();
            }

            return existingSet;
        }

        // ============================================================
        // PATH NORMALIZATION
        // ============================================================

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return path
                .ToLowerInvariant()
                .Trim()
                .Replace("/", "\\");
        }

        // ============================================================
        // SCAN
        // ============================================================

        private List<GameMetadata> ScanAndSelectGames()
        {
            var allFoundGames =
                new List<ScannedGameWrapper>();

            if (settings.Settings.ScanFolders == null)
                return new List<GameMetadata>();

            var blockedSet =
                new HashSet<string>();

            if (settings.Settings.BlockedPaths != null)
            {
                foreach (var path in settings.Settings.BlockedPaths)
                {
                    blockedSet.Add(
                        NormalizePath(path)
                    );
                }
            }

            var existingSet =
                BuildExistingGamesSet();

            foreach (var folder in settings.Settings.ScanFolders)
            {
                if (Directory.Exists(folder))
                {
                    allFoundGames.AddRange(
                        ScanFolderLimited(
                            folder,
                            blockedSet,
                            existingSet
                        )
                    );
                }
            }

            if (allFoundGames.Count == 0)
                return new List<GameMetadata>();

            List<GameMetadata> finalSelection =
                new List<GameMetadata>();

            Application.Current.Dispatcher.Invoke(() =>
            {
                var window =
                    new GameSelectionWindow(
                        allFoundGames
                    );

                if (Application.Current.MainWindow != null)
                {
                    window.Owner =
                        Application.Current.MainWindow;
                }

                if (window.ShowDialog() == true)
                {
                    var selectedGames =
                        window.SelectedGames;

                    finalSelection =
                        selectedGames
                            .Select(game => game.GameData)
                            .ToList();

                    // ====================================================
                    // METADATA TRACKING
                    // ====================================================

                    if (selectedGames.Count > 0)
                    {
                        pendingMetadataExecutablePath =
                            selectedGames[0].ExecutablePath;

                        logger.Info(
                            $"Waiting for metadata for: {pendingMetadataExecutablePath}"
                        );
                    }

                    // ====================================================
                    // HDR
                    // ====================================================

                    if (window.EnableHdrSupport &&
                        selectedGames.Count > 0)
                    {
                        pendingHdrExecutablePath =
                            selectedGames[0].ExecutablePath;

                        logger.Info(
                            $"HDR requested for: {pendingHdrExecutablePath}"
                        );
                    }
                    else
                    {
                        pendingHdrExecutablePath = null;
                    }

                    // ====================================================
                    // DS4WINDOWS CONTROLLER
                    // ====================================================

                    pendingControllerSelection =
                        window.SelectedController;

                    if (selectedGames.Count > 0)
                    {
                        foreach (var selectedGame in selectedGames)
                        {
                            UpdateDs4WindowsAutoProfile(
                                selectedGame.ExecutablePath,
                                pendingControllerSelection
                            );
                        }

                        logger.Info(
                            $"DS4Windows controller selection '{pendingControllerSelection}' applied to {selectedGames.Count} game(s) for user {Ds4WindowsUserName}."
                        );
                    }

                    // ====================================================
                    // RESHADE DEPLOYER
                    // ====================================================

                    if (selectedGames.Count > 0)
                    {
                        LaunchReShadeDeployer(
                            selectedGames[0].ExecutablePath
                        );
                    }

                    // ====================================================
                    // IGNORED GAMES
                    // ====================================================

                    var newlyIgnored =
                        allFoundGames
                            .Where(game => game.IsIgnored)
                            .Select(game =>
                                game.ExecutablePath)
                            .ToList();

                    if (newlyIgnored.Count > 0)
                    {
                        foreach (var path in newlyIgnored)
                        {
                            if (!settings.BlockedPathsUI
                                .Contains(path))
                            {
                                settings.BlockedPathsUI
                                    .Add(path);
                            }
                        }

                        settings.EndEdit();
                    }
                }
            });

            return finalSelection;
        }

        // ============================================================
        // DS4WINDOWS - AUTO PROFILE
        // ============================================================

        private void UpdateDs4WindowsAutoProfile(
            string executablePath,
            string controllerSelection)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    logger.Warn(
                        "Cannot update DS4Windows profile: executable path is empty."
                    );

                    return;
                }

                if (!File.Exists(executablePath))
                {
                    logger.Warn(
                        $"Cannot update DS4Windows profile: executable not found: {executablePath}"
                    );

                    return;
                }

                string autoProfilesPath =
                    Ds4WindowsAutoProfilesPath;

                string ds4Directory =
                    Path.GetDirectoryName(
                        Ds4WindowsExePath
                    );

                if (!File.Exists(Ds4WindowsExePath))
                {
                    logger.Warn(
                        $"DS4Windows executable not found for user {Ds4WindowsUserName}: {Ds4WindowsExePath}"
                    );

                    return;
                }

                if (!File.Exists(autoProfilesPath))
                {
                    logger.Warn(
                        $"DS4Windows Auto Profiles.xml not found: {autoProfilesPath}"
                    );

                    return;
                }

                // ========================================================
                // CONVERSION DU CHOIX DE LA LISTE
                // ========================================================

                string controllerValue;
                bool turnOff;

                switch (
                    (controllerSelection ?? string.Empty)
                        .Trim()
                        .ToUpperInvariant())
                {
                    case "PS4":
                        controllerValue = "PS4";
                        turnOff = false;
                        break;

                    case "XBOX":
                        controllerValue = "xbox";
                        turnOff = false;
                        break;

                    case "OFF":
                        controllerValue = "(none)";
                        turnOff = true;
                        break;

                    default:
                        logger.Warn(
                            $"Unknown DS4Windows controller selection: '{controllerSelection}'. No XML modification performed."
                        );

                        return;
                }

                // ========================================================
                // CHARGEMENT DU XML
                // ========================================================

                XDocument document =
                    XDocument.Load(
                        autoProfilesPath,
                        LoadOptions.PreserveWhitespace
                    );

                XElement root =
                    document.Element("Programs");

                if (root == null)
                {
                    logger.Warn(
                        $"Invalid DS4Windows Auto Profiles.xml: <Programs> root not found in {autoProfilesPath}"
                    );

                    return;
                }

                string normalizedTargetPath =
                    NormalizePath(executablePath);

                // ========================================================
                // RECHERCHE D'UNE ENTRÉE EXISTANTE
                // ========================================================

                XElement existingProgram =
                    root.Elements("Program")
                        .FirstOrDefault(program =>
                        {
                            string path =
                                (string)program.Attribute("path");

                            return NormalizePath(path) ==
                                normalizedTargetPath;
                        });

                if (existingProgram == null)
                {
                    // ====================================================
                    // NOUVELLE ENTRÉE
                    // ====================================================

                    existingProgram =
                        new XElement(
                            "Program",
                            new XAttribute(
                                "path",
                                executablePath
                            ),
                            new XAttribute(
                                "title",
                                ""
                            ),
                            new XElement(
                                "Controller1",
                                controllerValue
                            ),
                            new XElement(
                                "Controller2",
                                "(none)"
                            ),
                            new XElement(
                                "Controller3",
                                "(none)"
                            ),
                            new XElement(
                                "Controller4",
                                "(none)"
                            ),
                            new XElement(
                                "Controller5",
                                "(none)"
                            ),
                            new XElement(
                                "Controller6",
                                "(none)"
                            ),
                            new XElement(
                                "Controller7",
                                "(none)"
                            ),
                            new XElement(
                                "Controller8",
                                "(none)"
                            ),
                            new XElement(
                                "TurnOff",
                                turnOff ? "True" : "False"
                            )
                        );

                    root.Add(existingProgram);

                    logger.Info(
                        $"Added DS4Windows Auto Profile for: {executablePath} | Controller1={controllerValue} | TurnOff={turnOff}"
                    );
                }
                else
                {
                    // ====================================================
                    // MISE À JOUR D'UNE ENTRÉE EXISTANTE
                    // ====================================================

                    SetXmlElementValue(
                        existingProgram,
                        "Controller1",
                        controllerValue
                    );

                    SetXmlElementValue(
                        existingProgram,
                        "TurnOff",
                        turnOff ? "True" : "False"
                    );

                    // On s'assure que Controller2-8 existent.
                    EnsureControllerElement(
                        existingProgram,
                        "Controller2"
                    );

                    EnsureControllerElement(
                        existingProgram,
                        "Controller3"
                    );

                    EnsureControllerElement(
                        existingProgram,
                        "Controller4"
                    );

                    EnsureControllerElement(
                        existingProgram,
                        "Controller5"
                    );

                    EnsureControllerElement(
                        existingProgram,
                        "Controller6"
                    );

                    EnsureControllerElement(
                        existingProgram,
                        "Controller7"
                    );

                    EnsureControllerElement(
                        existingProgram,
                        "Controller8"
                    );

                    logger.Info(
                        $"Updated DS4Windows Auto Profile for: {executablePath} | Controller1={controllerValue} | TurnOff={turnOff}"
                    );
                }

                // ========================================================
                // SAUVEGARDE
                // ========================================================

                document.Save(
                    autoProfilesPath,
                    SaveOptions.DisableFormatting
                );

                logger.Info(
                    $"DS4Windows Auto Profiles.xml successfully saved: {autoProfilesPath}"
                );
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    $"Failed to update DS4Windows Auto Profiles.xml for: {executablePath}"
                );
            }
        }

        // ============================================================
        // XML HELPER
        // ============================================================

        private void SetXmlElementValue(
            XElement parent,
            string elementName,
            string value)
        {
            XElement element =
                parent.Element(elementName);

            if (element == null)
            {
                parent.Add(
                    new XElement(
                        elementName,
                        value
                    )
                );
            }
            else
            {
                element.Value = value;
            }
        }

        // ============================================================
        // XML CONTROLLER HELPER
        // ============================================================

        private void EnsureControllerElement(
            XElement parent,
            string controllerName)
        {
            XElement element =
                parent.Element(controllerName);

            if (element == null)
            {
                parent.Add(
                    new XElement(
                        controllerName,
                        "(none)"
                    )
                );
            }
        }

        // ============================================================
        // RESHADE DEPLOYER
        // ============================================================

        private void LaunchReShadeDeployer(
            string executablePath)
        {
            try
            {
                if (!File.Exists(ReShadeDeployerPath))
                {
                    logger.Warn(
                        $"ReShade Deployer not found: {ReShadeDeployerPath}"
                    );

                    return;
                }

                if (string.IsNullOrWhiteSpace(
                    executablePath) ||
                    !File.Exists(executablePath))
                {
                    logger.Warn(
                        $"Target executable not found: {executablePath}"
                    );

                    return;
                }

                var startInfo =
                    new ProcessStartInfo
                    {
                        FileName =
                            ReShadeDeployerPath,

                        Arguments =
                            $"\"{executablePath}\"",

                        UseShellExecute = true
                    };

                Process.Start(startInfo);

                logger.Info(
                    $"Started ReShade Deployer for: {executablePath}"
                );
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    $"Failed to start ReShade Deployer for: {executablePath}"
                );
            }
        }

        // ============================================================
        // RECURSIVE FOLDER SCAN
        // ============================================================

        private IEnumerable<ScannedGameWrapper>
            ScanFolderLimited(
                string rootPath,
                HashSet<string> blockedSet,
                HashSet<string> existingSet)
        {
            var results =
                new List<ScannedGameWrapper>();

            results.AddRange(
                GetExecutablesInDir(
                    rootPath,
                    blockedSet,
                    existingSet
                )
            );

            try
            {
                foreach (var subDir in
                    Directory.GetDirectories(rootPath))
                {
                    results.AddRange(
                        ScanFolderLimited(
                            subDir,
                            blockedSet,
                            existingSet
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                logger.Warn(
                    ex,
                    $"Failed to scan subdirectories in: {rootPath}"
                );
            }

            return results;
        }

        // ============================================================
        // FIND EXE
        // ============================================================

        private IEnumerable<ScannedGameWrapper>
            GetExecutablesInDir(
                string dirPath,
                HashSet<string> blockedSet,
                HashSet<string> existingSet)
        {
            var list =
                new List<ScannedGameWrapper>();

            try
            {
                var files =
                    Directory.GetFiles(
                        dirPath,
                        "*.exe"
                    );

                foreach (var file in files)
                {
                    string normalizedFile =
                        NormalizePath(file);

                    string normalizedDir =
                        NormalizePath(dirPath);

                    bool isIgnored =
                        blockedSet.Contains(
                            normalizedFile
                        ) ||
                        blockedSet.Contains(
                            normalizedDir
                        );

                    if (isIgnored)
                        continue;

                    bool alreadyExists =
                        existingSet.Contains(
                            normalizedFile
                        ) ||
                        existingSet.Contains(
                            normalizedDir
                        );

                    if (alreadyExists)
                        continue;

                    if (IsGameExecutable(file))
                    {
                        var fileInfo =
                            new FileInfo(file);

                        string gameName =
                            GetGameNameFromFolder(
                                dirPath
                            );

                        var metadata =
                            new GameMetadata
                            {
                                Name = gameName,

                                GameId =
                                    fileInfo.FullName,

                                InstallDirectory =
                                    fileInfo.DirectoryName,

                                IsInstalled = true,

                                Platforms =
                                    new HashSet<MetadataProperty>
                                    {
                                        new MetadataSpecProperty(
                                            "pc_windows"
                                        )
                                    },

                                Source =
                                    new MetadataNameProperty(
                                        "AutoImport"
                                    ),

                                GameActions =
                                    new List<GameAction>
                                    {
                                        new GameAction
                                        {
                                            Type =
                                                GameActionType.File,

                                            Path =
                                                fileInfo.FullName,

                                            WorkingDir =
                                                fileInfo.DirectoryName,

                                            Name =
                                                "Play",

                                            IsPlayAction =
                                                true
                                        }
                                    }
                            };

                        list.Add(
                            new ScannedGameWrapper
                            {
                                GameData =
                                    metadata
                            }
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(
                    ex,
                    $"Failed to scan directory for executables: {dirPath}"
                );
            }

            return list;
        }

        // ============================================================
        // GAME NAME
        // ============================================================

        private string GetGameNameFromFolder(
            string dirPath)
        {
            string folderName =
                Path.GetFileName(
                    dirPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    )
                );

            if (string.IsNullOrWhiteSpace(folderName))
            {
                return "Unknown Game";
            }

            string cleanFolderName =
                CleanGameName(folderName);

            if (!string.IsNullOrWhiteSpace(cleanFolderName))
            {
                return cleanFolderName;
            }

            // Aucun fallback vers le nom de l'EXE.
            // On garde au minimum le nom original du dossier.
            return folderName.Trim();
        }

        // ============================================================
        // VALID GAME NAME
        // ============================================================

        private bool IsValidGameName(
            string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (name.Length < 2)
                return false;

            string lowerName =
                name.ToLowerInvariant();

            string[] genericNames =
            {
                "bin",
                "game",
                "games",
                "exe",
                "exes",
                "program",
                "programs",
                "application",
                "applications",
                "software",
                "tools",
                "util",
                "utils",
                "temp",
                "tmp",
                "download",
                "downloads"
            };

            return !genericNames.Contains(
                lowerName
            );
        }

        // ============================================================
        // CLEAN GAME NAME
        // ============================================================

        private string CleanGameName(
            string filename)
        {
            if (string.IsNullOrWhiteSpace(
                filename))
            {
                return filename;
            }

            string clean =
                filename.Trim();

            // Remplace les séparateurs courants par des espaces.
            clean = clean
                .Replace('.', ' ')
                .Replace('_', ' ');

            // Supprime les informations entre crochets / parenthèses.
            clean = Regex.Replace(
                clean,
                @"\[.*?\]|\(.*?\)",
                " ",
                RegexOptions.IgnoreCase
            );

            // Versions et informations techniques inutiles.
            string junkPattern =
                @"\bv?\d+(\.\d+)+\b" +
                @"|\brepack\b" +
                @"|\bgoty\b" +
                @"|\bx64\b" +
                @"|\bx86\b" +
                @"|\bbuild\b" +
                @"|\bsetup\b" +
                @"|\binstaller\b";

            clean = Regex.Replace(
                clean,
                junkPattern,
                " ",
                RegexOptions.IgnoreCase
            );

            // Normalisation des différentes écritures courantes.
            clean = Regex.Replace(
                clean,
                @"\bdirectors\s+cut\b",
                "Director's Cut",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bdirector\s+cut\b",
                "Director's Cut",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bgame\s+of\s+the\s+year\b",
                "Game of the Year",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bdefinitive\s+edition\b",
                "Definitive Edition",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bcomplete\s+edition\b",
                "Complete Edition",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bultimate\s+edition\b",
                "Ultimate Edition",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bgoty\b",
                "Game of the Year",
                RegexOptions.IgnoreCase
            );

            // Nettoyage des espaces.
            clean = Regex.Replace(
                clean,
                @"\s+",
                " "
            ).Trim();

            if (string.IsNullOrWhiteSpace(clean))
                return string.Empty;

            // Capitalisation douce :
            // on conserve les mots déjà contenant des majuscules
            // internes et on normalise surtout les noms entièrement
            // en majuscules provenant des dossiers.
            if (clean == clean.ToUpperInvariant())
            {
                clean =
                    ToTitleCasePreservingSpecialWords(
                        clean.ToLowerInvariant()
                    );
            }

            return clean.Trim();
        }

        // ============================================================
        // TITLE CASE
        // ============================================================

        private string ToTitleCasePreservingSpecialWords(
            string text)
        {
            string[] words =
                text.Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries
                );

            for (int i = 0; i < words.Length; i++)
            {
                string word =
                    words[i];

                if (string.IsNullOrWhiteSpace(word))
                    continue;

                if (word.Contains("'"))
                {
                    string[] parts =
                        word.Split(
                            new[] { '\'' },
                            StringSplitOptions.None
                        );

                    for (int j = 0; j < parts.Length; j++)
                    {
                        if (!string.IsNullOrEmpty(parts[j]))
                        {
                            parts[j] =
                                char.ToUpper(
                                    parts[j][0]
                                ) +
                                parts[j].Substring(1);
                        }
                    }

                    words[i] =
                        string.Join(
                            "'",
                            parts
                        );
                }
                else
                {
                    words[i] =
                        char.ToUpper(word[0]) +
                        word.Substring(1);
                }
            }

            string result =
                string.Join(
                    " ",
                    words
                );

            // Réapplique les formes spéciales.
            result = Regex.Replace(
                result,
                @"\bDirectors\s+Cut\b",
                "Director's Cut",
                RegexOptions.IgnoreCase
            );

            result = Regex.Replace(
                result,
                @"\bDirector\s+Cut\b",
                "Director's Cut",
                RegexOptions.IgnoreCase
            );

            result = Regex.Replace(
                result,
                @"\bGame\s+Of\s+The\s+Year\b",
                "Game of the Year",
                RegexOptions.IgnoreCase
            );

            return result;
        }

        // ============================================================
        // EXE FILTER
        // ============================================================

        private bool IsGameExecutable(
            string path)
        {
            string fileName =
                Path.GetFileName(path)
                    .ToLower();

            return !(
                fileName.Contains("uninstall") ||
                fileName.Contains("setup") ||
                fileName.Contains("config") ||
                fileName.Contains("crash")
            );
        }
    }
}
