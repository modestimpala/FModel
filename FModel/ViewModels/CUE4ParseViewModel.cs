using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdonisUI.Controls;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.KRD.Assets.Exports;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Oodle.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Wwise;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Sounds;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Kismet;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;
using EpicManifestParser;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using FModel.Creator;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.Views;
using FModel.Views.Resources.Controls;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Serilog;
using SkiaSharp;
using UE4Config.Parsing;
using Application = System.Windows.Application;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;

namespace FModel.ViewModels;

public class CUE4ParseViewModel : ViewModel
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;

    private bool _isVerse;

    private readonly Regex _fnLiveRegex = new(@"^FortniteGame[/\\]Content[/\\]Paks[/\\]",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private bool _modelIsOverwritingMaterial;

    public bool ModelIsOverwritingMaterial
    {
        get => _modelIsOverwritingMaterial;
        set => SetProperty(ref _modelIsOverwritingMaterial, value);
    }

    public bool IsSnooperOpen => _snooper is { Exists: true, IsVisible: true };
    private Snooper _snooper;

    public Snooper SnooperViewer
    {
        get
        {
            if (_snooper != null) return _snooper;

            return Application.Current.Dispatcher.Invoke(delegate
            {
                var scale = ImGuiController.GetDpiScale();
                var htz = Snooper.GetMaxRefreshFrequency();
                return _snooper = new Snooper(
                    new GameWindowSettings { UpdateFrequency = htz },
                    new NativeWindowSettings
                    {
                        ClientSize = new OpenTK.Mathematics.Vector2i(
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenWidth * .75 * scale),
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenHeight * .85 * scale)),
                        NumberOfSamples = Constants.SAMPLES_COUNT,
                        WindowBorder = WindowBorder.Resizable,
                        Flags = ContextFlags.ForwardCompatible,
                        Profile = ContextProfile.Core,
                        Vsync = VSyncMode.Adaptive,
                        APIVersion = new Version(4, 6),
                        StartVisible = false,
                        StartFocused = false,
                        Title = "3D Viewer"
                    });
            });
        }
    }

    public AbstractVfsFileProvider Provider { get; }
    public GameDirectoryViewModel GameDirectory { get; }
    public AssetsFolderViewModel AssetsFolder { get; }
    public SearchViewModel SearchVm { get; }
    public TabControlViewModel TabControl { get; }
    public ConfigIni IoStoreOnDemand { get; }

    public CUE4ParseViewModel()
    {
        var currentDir = UserSettings.Default.CurrentDir;
        var gameDirectory = currentDir.GameDirectory;
        var versionContainer = new VersionContainer(
            game: currentDir.UeVersion, platform: currentDir.TexturePlatform,
            customVersions: new FCustomVersionContainer(currentDir.Versioning.CustomVersions),
            optionOverrides: currentDir.Versioning.Options,
            mapStructTypesOverrides: currentDir.Versioning.MapStructTypes);
        var pathComparer = StringComparer.OrdinalIgnoreCase;

        switch (gameDirectory)
        {
            case Constants._FN_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("FortniteLive", versionContainer, pathComparer);
                break;
            }
            case Constants._VAL_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("ValorantLive", versionContainer, pathComparer);
                break;
            }
            default:
            {
                var project = gameDirectory
                    .SubstringBeforeLast(gameDirectory.Contains("eFootball") ? "\\pak" : "\\Content")
                    .SubstringAfterLast("\\");
                Provider = project switch
                {
                    "StateOfDecay2" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) +
                            "\\StateOfDecay2\\Saved\\Paks"),
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) +
                            "\\StateOfDecay2\\Saved\\DisabledPaks")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    "eFootball" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) +
                            "\\KONAMI\\eFootball\\ST\\Download")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    _ => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer,
                        pathComparer)
                };

                break;
            }
        }

        Provider.ReadScriptData = true;
        Provider.ReadShaderMaps = UserSettings.Default.ReadShaderMaps;
        Provider.ReadNaniteData = true;

        GameDirectory = new GameDirectoryViewModel();
        AssetsFolder = new AssetsFolderViewModel();
        SearchVm = new SearchViewModel();
        TabControl = new TabControlViewModel();
        IoStoreOnDemand = new ConfigIni(nameof(IoStoreOnDemand));
    }

    public async Task Initialize()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            switch (Provider)
            {
                case StreamedFileProvider p:
                    switch (p.LiveGame)
                    {
                        case "FortniteLive":
                        {
                            var manifestInfo = _apiEndpointView.EpicApi.GetManifest(cancellationToken);
                            if (manifestInfo is null)
                            {
                                throw new FileLoadException(
                                    "Could not load latest Fortnite manifest, you may have to switch to your local installation.");
                            }

                            var cacheDir = Directory
                                .CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")).FullName;
                            var manifestOptions = new ManifestParseOptions
                            {
                                ChunkCacheDirectory = cacheDir,
                                ManifestCacheDirectory = cacheDir,
                                ChunkBaseUrl = "http://download.epicgames.com/Builds/Fortnite/CloudDir/",
                                Decompressor = ManifestZlibngDotNetDecompressor.Decompress,
                                DecompressorState = ZlibHelper.Instance,
                                CacheChunksAsIs = false
                            };

                            var startTs = Stopwatch.GetTimestamp();
                            FBuildPatchAppManifest manifest;

                            try
                            {
                                (manifest, _) = manifestInfo.DownloadAndParseAsync(manifestOptions,
                                    cancellationToken: cancellationToken,
                                    elementManifestPredicate: static x => x.Uri.Host == "download.epicgames.com"
                                ).GetAwaiter().GetResult();
                            }
                            catch (HttpRequestException ex)
                            {
                                Log.Error("Failed to download manifest ({ManifestUri})",
                                    ex.Data["ManifestUri"]?.ToString() ?? "");
                                throw;
                            }

                            if (manifest.TryFindFile("Cloud/IoStoreOnDemand.ini", out var ioStoreOnDemandFile))
                            {
                                IoStoreOnDemand.Read(new StreamReader(ioStoreOnDemandFile.GetStream()));
                            }

                            Parallel.ForEach(manifest.Files.Where(x => _fnLiveRegex.IsMatch(x.FileName)),
                                fileManifest =>
                                {
                                    p.RegisterVfs(fileManifest.FileName, [fileManifest.GetStream()],
                                        it => new FRandomAccessStreamArchive(it, manifest.FindFile(it)!.GetStream(),
                                            p.Versions));
                                });

                            var elapsedTime = Stopwatch.GetElapsedTime(startTs);
                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text(
                                    $"Fortnite [LIVE] has been loaded successfully in {elapsedTime.TotalMilliseconds:F1}ms",
                                    Constants.WHITE, true));
                            break;
                        }
                        case "ValorantLive":
                        {
                            var manifest = _apiEndpointView.ValorantApi.GetManifest(cancellationToken);
                            if (manifest == null)
                            {
                                throw new Exception(
                                    "Could not load latest Valorant manifest, you may have to switch to your local installation.");
                            }

                            Parallel.ForEach(manifest.Paks, pak =>
                            {
                                p.RegisterVfs(pak.GetFullName(), [pak.GetStream(manifest)]);
                            });

                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text($"Valorant '{manifest.Header.GameVersion}' has been loaded successfully",
                                    Constants.WHITE, true));
                            break;
                        }
                    }

                    break;
                case DefaultFileProvider:
                {
                    var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory,
                        "..\\..\\..\\Cloud\\IoStoreOnDemand.ini");
                    if (File.Exists(ioStoreOnDemandPath))
                    {
                        using var s = new StreamReader(ioStoreOnDemandPath);
                        IoStoreOnDemand.Read(s);
                    }

                    break;
                }
            }

            Provider.Initialize();
            Log.Information(
                $"{Provider.Versions.Game} ({Provider.Versions.Platform}) | Archives: x{Provider.UnloadedVfs.Count} | AES: x{Provider.RequiredKeys.Count} | Loose Files: x{Provider.Files.Count}");
        });
    }

    /// <summary>
    /// load virtual files system from GameDirectory
    /// </summary>
    /// <returns></returns>
    public void LoadVfs(IEnumerable<KeyValuePair<FGuid, FAesKey>> aesKeys)
    {
        Provider.SubmitKeys(aesKeys);
        Provider.PostMount();

        var aesMax = Provider.RequiredKeys.Count + Provider.Keys.Count;
        var archiveMax = Provider.UnloadedVfs.Count + Provider.MountedVfs.Count;
        Log.Information(
            $"Project: {Provider.ProjectName} | Mounted: {Provider.MountedVfs.Count}/{archiveMax} | AES: {Provider.Keys.Count}/{aesMax} | Files: x{Provider.Files.Count}");
    }

    public void ClearProvider()
    {
        if (Provider == null) return;

        AssetsFolder.Folders.Clear();
        SearchVm.SearchResults.Clear();
        Helper.CloseWindow<AdonisWindow>("Search View");
        Provider.UnloadNonStreamedVfs();
        GC.Collect();
    }

    public async Task RefreshAes()
    {
        // game directory dependent, we don't have the provider game name yet since we don't have aes keys
        // except when this comes from the AES Manager
        if (!UserSettings.IsEndpointValid(EEndpointType.Aes, out var endpoint))
            return;

        await _threadWorkerView.Begin(cancellationToken =>
        {
            var aes = _apiEndpointView.DynamicApi.GetAesKeys(cancellationToken, endpoint.Url, endpoint.Path);
            if (aes is not { IsValid: true }) return;

            UserSettings.Default.CurrentDir.AesKeys = aes;
        });
    }

    public async Task InitInformation()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            var info = _apiEndpointView.FModelApi.GetNews(cancellationToken, Provider.ProjectName);
            if (info == null) return;

            FLogger.Append(ELog.None, () =>
            {
                for (var i = 0; i < info.Messages.Length; i++)
                {
                    FLogger.Text(info.Messages[i], info.Colors[i], bool.Parse(info.NewLines[i]));
                }
            });
        });
    }

    public Task InitMappings(bool force = false)
    {
        if (!UserSettings.IsEndpointValid(EEndpointType.Mapping, out var endpoint))
        {
            Provider.MappingsContainer = null;
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            var l = ELog.Information;
            if (endpoint.Overwrite && File.Exists(endpoint.FilePath))
            {
                Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(endpoint.FilePath);
            }
            else if (endpoint.IsValid)
            {
                var mappingsFolder = Path.Combine(UserSettings.Default.OutputDirectory, ".data");
                if (endpoint.Path == "$.[?(@.meta.compressionMethod=='Oodle')].['url','fileName']")
                    endpoint.Path = "$.[0].['url','fileName']";
                var mappings = _apiEndpointView.DynamicApi.GetMappings(default, endpoint.Url, endpoint.Path);
                if (mappings is { Length: > 0 })
                {
                    foreach (var mapping in mappings)
                    {
                        if (!mapping.IsValid) continue;

                        var mappingPath = Path.Combine(mappingsFolder, mapping.FileName);
                        if (force || !File.Exists(mappingPath))
                        {
                            _apiEndpointView.DownloadFile(mapping.Url, mappingPath);
                        }

                        Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingPath);
                        break;
                    }
                }

                if (Provider.MappingsContainer == null)
                {
                    var latestUsmaps = new DirectoryInfo(mappingsFolder).GetFiles("*_oo.usmap");
                    if (latestUsmaps.Length <= 0) return;

                    var latestUsmapInfo = latestUsmaps.OrderBy(f => f.LastWriteTime).Last();
                    Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(latestUsmapInfo.FullName);
                    l = ELog.Warning;
                }
            }

            if (Provider.MappingsContainer is FileUsmapTypeMappingsProvider m)
            {
                Log.Information($"Mappings pulled from '{m.FileName}'");
                FLogger.Append(l, () => FLogger.Text($"Mappings pulled from '{m.FileName}'", Constants.WHITE, true));
            }
        });
    }

    public Task VerifyConsoleVariables()
    {
        if (Provider.Versions["StripAdditiveRefPose"])
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text(
                    "Additive animations have their reference pose stripped, which will lead to inaccurate preview and export",
                    Constants.WHITE, true));
        }

        if (Provider.Versions.Game is EGame.GAME_UE4_LATEST or EGame.GAME_UE5_LATEST &&
            !Provider.ProjectName.Equals("FortniteGame",
                StringComparison.OrdinalIgnoreCase)) // ignore fortnite globally
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text(
                    $"Experimental UE version selected, likely unsuitable for '{Provider.GameDisplayName ?? Provider.ProjectName}'",
                    Constants.WHITE, true));
        }

        return Task.CompletedTask;
    }

    public Task VerifyOnDemandArchives()
    {
        // only local fortnite
        if (Provider is not DefaultFileProvider ||
            !Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        // scuffed but working
        var persistentDownloadDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FortniteGame/Saved/PersistentDownloadDir");
        var iasFileInfo = new FileInfo(Path.Combine(persistentDownloadDir, "ias", "ias.cache.0"));
        if (!iasFileInfo.Exists || iasFileInfo.Length == 0)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            var inst = new List<InstructionToken>();
            IoStoreOnDemand.FindPropertyInstructions("Endpoint", "TocPath", inst);
            if (inst.Count <= 0) return;

            var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud",
                inst[0].Value.SubstringAfterLast("/").SubstringBefore("\""));
            if (!File.Exists(ioStoreOnDemandPath)) return;

            await _apiEndpointView.EpicApi.VerifyAuth(default);
            await Provider.RegisterVfs(new IoChunkToc(ioStoreOnDemandPath),
                new IoStoreOnDemandOptions
                {
                    ChunkBaseUri = new Uri("https://download.epicgames.com/ias/fortnite/", UriKind.Absolute),
                    ChunkCacheDirectory =
                        Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")),
                    Authorization =
                        new AuthenticationHeaderValue("Bearer", UserSettings.Default.LastAuthResponse.AccessToken),
                    Timeout = TimeSpan.FromSeconds(30)
                });
            var onDemandCount = await Provider.MountAsync();
            FLogger.Append(ELog.Information, () =>
                FLogger.Text(
                    $"{onDemandCount} on-demand archive{(onDemandCount > 1 ? "s" : "")} streamed via epicgames.com",
                    Constants.WHITE, true));
        });
    }

    public int LocalizedResourcesCount { get; set; }
    public bool LocalResourcesDone { get; set; }
    public bool HotfixedResourcesDone { get; set; }

    public async Task LoadLocalizedResources()
    {
        var snapshot = LocalizedResourcesCount;
        await Task.WhenAll(LoadGameLocalizedResources(), LoadHotfixedLocalizedResources()).ConfigureAwait(false);

        LocalizedResourcesCount = Provider.Internationalization.Count;
        if (snapshot != LocalizedResourcesCount)
        {
            FLogger.Append(ELog.Information, () =>
                FLogger.Text(
                    $"{LocalizedResourcesCount} localized resources loaded for '{UserSettings.Default.AssetLanguage.GetDescription()}'",
                    Constants.WHITE, true));
            Utils.Typefaces = new Typefaces(this);
        }
    }

    private Task LoadGameLocalizedResources()
    {
        if (LocalResourcesDone) return Task.CompletedTask;
        return Task.Run(() =>
        {
            LocalResourcesDone =
                Provider.TryChangeCulture(Provider.GetLanguageCode(UserSettings.Default.AssetLanguage));
        });
    }

    private Task LoadHotfixedLocalizedResources()
    {
        if (!Provider.ProjectName.Equals("fortnitegame", StringComparison.OrdinalIgnoreCase) || HotfixedResourcesDone)
            return Task.CompletedTask;
        return Task.Run(() =>
        {
            var hotfixes = ApplicationService.ApiEndpointView.CentralApi.GetHotfixes(default,
                Provider.GetLanguageCode(UserSettings.Default.AssetLanguage));
            if (hotfixes == null) return;

            Provider.Internationalization.Override(hotfixes);
            HotfixedResourcesDone = true;
        });
    }

    private int _virtualPathCount { get; set; }

    public Task LoadVirtualPaths()
    {
        if (_virtualPathCount > 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            _virtualPathCount = Provider.LoadVirtualPaths(UserSettings.Default.CurrentDir.UeVersion.GetVersion());
            if (_virtualPathCount > 0)
            {
                FLogger.Append(ELog.Information, () =>
                    FLogger.Text($"{_virtualPathCount} virtual paths loaded", Constants.WHITE, true));
            }
            else
            {
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text("Could not load virtual paths, plugin manifest may not exist", Constants.WHITE, true));
            }
        });
    }

    public void ExtractSelected(CancellationToken cancellationToken, IEnumerable<GameFile> assetItems)
    {
        foreach (var entry in assetItems)
        {
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Extract(cancellationToken, entry, TabControl.HasNoTabs);
        }
    }

    private void BulkFolder(CancellationToken cancellationToken, TreeItem folder, Action<GameFile> action)
    {
        foreach (var entry in folder.AssetsList.Assets)
        {
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                action(entry);
            }
            catch
            {
                // ignore
            }
        }

        foreach (var f in folder.Folders) BulkFolder(cancellationToken, f, action);
    }

    public void ExportFolder(CancellationToken cancellationToken, TreeItem folder)
    {
        Parallel.ForEach(folder.AssetsList.Assets, entry =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportData(entry, false);
        });

        foreach (var f in folder.Folders) ExportFolder(cancellationToken, f);
    }

    public void ExtractFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs));

    public void SaveFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder,
            asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Properties | EBulkType.Auto));

    public void TextureFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder,
            asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Textures | EBulkType.Auto));

    public void ModelFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder,
            asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Meshes | EBulkType.Auto));

    public void AnimationFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder,
            asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Animations | EBulkType.Auto));

    public void Extract(CancellationToken cancellationToken, GameFile entry, bool addNewTab = false,
        EBulkType bulk = EBulkType.None)
    {
        Log.Information("User DOUBLE-CLICKED to extract '{FullPath}'", entry.Path);

        if (addNewTab && TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(entry.Extension);

        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveProperties = HasFlag(bulk, EBulkType.Properties);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        switch (entry.Extension)
        {
            case "uasset":
            case "umap":
            {
                var result = Provider.GetLoadPackageResult(entry);
                TabControl.SelectedTab.TitleExtra = result.TabTitleExtra;

                if (saveProperties || updateUi)
                {
                    TabControl.SelectedTab.SetDocumentText(
                        JsonConvert.SerializeObject(result.GetDisplayData(saveProperties), Formatting.Indented),
                        saveProperties, updateUi);
                    if (saveProperties) break; // do not search for viewable exports if we are dealing with jsons
                }

                for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
                {
                    if (CheckExport(cancellationToken, result.Package, i, bulk))
                        break;
                }

                break;
            }
            case "upluginmanifest":
            case "uproject":
            case "manifest":
            case "uplugin":
            case "archive":
            case "dnearchive": // Banishers: Ghosts of New Eden
            case "vmodule":
            case "uparam": // Steel Hunters
            case "verse":
            case "html":
            case "json":
            case "ini":
            case "txt":
            case "log":
            case "lsd": // Days Gone
            case "bat":
            case "dat":
            case "cfg":
            case "ddr":
            case "ide":
            case "ipl":
            case "zon":
            case "xml":
            case "css":
            case "csv":
            case "pem":
            case "tps":
            case "tgc": // State of Decay 2
            case "lua":
            case "js":
            case "po":
            case "h":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                using var reader = new StreamReader(stream);

                TabControl.SelectedTab.SetDocumentText(reader.ReadToEnd(), saveProperties, updateUi);

                break;
            }
            case "locmeta":
            {
                var archive = entry.CreateReader();
                var metadata = new FTextLocalizationMetaDataResource(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(metadata, Formatting.Indented),
                    saveProperties, updateUi);

                break;
            }
            case "locres":
            {
                var archive = entry.CreateReader();
                var locres = new FTextLocalizationResource(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(locres, Formatting.Indented),
                    saveProperties, updateUi);

                break;
            }
            case "bin" when entry.Name.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase):
            {
                var archive = entry.CreateReader();
                var registry = new FAssetRegistryState(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented),
                    saveProperties, updateUi);

                break;
            }
            case "bin" when entry.Name.Contains("GlobalShaderCache", StringComparison.OrdinalIgnoreCase):
            {
                var archive = entry.CreateReader();
                var registry = new FGlobalShaderCache(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented),
                    saveProperties, updateUi);

                break;
            }
            case "bnk":
            case "pck":
            {
                var archive = entry.CreateReader();
                var wwise = new WwiseReader(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(wwise, Formatting.Indented),
                    saveProperties, updateUi);
                foreach (var (name, data) in wwise.WwiseEncodedMedias)
                {
                    SaveAndPlaySound(entry.Path.SubstringBeforeWithLast('/') + name, "WEM", data);
                }

                break;
            }
            case "xvag":
            case "at9":
            case "wem":
            {
                var data = Provider.SaveAsset(entry);
                SaveAndPlaySound(entry.PathWithoutExtension, entry.Extension, data);

                break;
            }
            case "udic":
            {
                var archive = entry.CreateReader();
                var header = new FOodleDictionaryArchive(archive).Header;
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(header, Formatting.Indented),
                    saveProperties, updateUi);

                break;
            }
            case "png":
            case "jpg":
            case "bmp":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                TabControl.SelectedTab.AddImage(entry.NameWithoutExtension, false, SKBitmap.Decode(stream),
                    saveTextures, updateUi);

                break;
            }
            case "svg":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                var svg = new SkiaSharp.Extended.Svg.SKSvg(new SKSize(512, 512));
                svg.Load(stream);

                var bitmap = new SKBitmap(512, 512);
                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium })
                {
                    canvas.DrawPicture(svg.Picture, paint);
                }

                TabControl.SelectedTab.AddImage(entry.NameWithoutExtension, false, bitmap, saveTextures, updateUi);

                break;
            }
            case "ufont":
            case "otf":
            case "ttf":
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text(
                        $"Export '{entry.Name}' raw data and change its extension if you want it to be an installable font file",
                        Constants.WHITE, true));
                break;
            case "ushaderbytecode":
            case "ushadercode":
            {
                var archive = entry.CreateReader();
                var ar = new FShaderCodeArchive(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented),
                    saveProperties, updateUi);

                break;
            }
            case "upipelinecache":
            {
                var archive = entry.CreateReader();
                var ar = new FPipelineCacheFile(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented),
                    saveProperties, updateUi);

                break;
            }
            case "res": // just skip
                break;
            default:
            {
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text($"The package '{entry.Name}' is of an unknown type.", Constants.WHITE, true));
                break;
            }
        }
    }

    public void ExtractAndScroll(CancellationToken cancellationToken, string fullPath, string objectName,
        string parentExportType)
    {
        Log.Information("User CTRL-CLICKED to extract '{FullPath}'", fullPath);

        var entry = Provider[fullPath];
        TabControl.AddTab(entry, parentExportType);
        TabControl.SelectedTab.ScrollTrigger = objectName;

        var result = Provider.GetLoadPackageResult(entry, objectName);

        TabControl.SelectedTab.TitleExtra = result.TabTitleExtra;
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(""); // json
        TabControl.SelectedTab.SetDocumentText(
            JsonConvert.SerializeObject(result.GetDisplayData(), Formatting.Indented), false, false);

        for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
        {
            if (CheckExport(cancellationToken, result.Package, i))
                break;
        }
    }

    private bool CheckExport(CancellationToken cancellationToken, IPackage pkg, int index,
        EBulkType bulk = EBulkType.None) // return true once you wanna stop searching for exports
    {
        var isNone = bulk == EBulkType.None;
        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);

        var pointer = new FPackageIndex(pkg, index + 1).ResolvedObject;
        if (pointer?.Object is null) return false;

        var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class?.Object?.Value as UStruct, pkg);
        switch (dummy)
        {
            case UVerseDigest when isNone && pointer.Object.Value is UVerseDigest verseDigest:
            {
                if (!TabControl.CanAddTabs) return false;

                TabControl.AddTab($"{verseDigest.ProjectName}.verse");
                TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("verse");
                TabControl.SelectedTab.SetDocumentText(verseDigest.ReadableCode, false, false);
                return true;
            }
            case UTexture when (isNone || saveTextures) && pointer.Object.Value is UTexture texture:
            {
                TabControl.SelectedTab.AddImage(texture, saveTextures, updateUi);
                return false;
            }
            case USvgAsset when (isNone || saveTextures) && pointer.Object.Value is USvgAsset svgasset:
            {
                const int size = 512;
                var data = svgasset.GetOrDefault<byte[]>("SvgData");
                var sourceFile = svgasset.GetOrDefault<string>("SourceFile");
                using var stream = new MemoryStream(data) { Position = 0 };
                var svg = new SkiaSharp.Extended.Svg.SKSvg(new SKSize(size, size));
                svg.Load(stream);

                var bitmap = new SKBitmap(size, size);
                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium })
                {
                    canvas.DrawPicture(svg.Picture, paint);
                }

                if (saveTextures)
                {
                    var fileName = sourceFile.SubstringAfterLast('/');
                    var path = Path.Combine(UserSettings.Default.TextureDirectory,
                        UserSettings.Default.KeepDirectoryStructure ? TabControl.SelectedTab.Entry.Directory : "",
                        fileName!).Replace('\\', '/');

                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));

                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    fs.Write(data, 0, data.Length);
                    if (File.Exists(path))
                    {
                        Log.Information("{FileName} successfully saved", fileName);
                        if (updateUi)
                        {
                            FLogger.Append(ELog.Information, () =>
                            {
                                FLogger.Text("Successfully saved ", Constants.WHITE);
                                FLogger.Link(fileName, path, true);
                            });
                        }
                    }
                    else
                    {
                        Log.Error("{FileName} could not be saved", fileName);
                        if (updateUi)
                            FLogger.Append(ELog.Error,
                                () => FLogger.Text($"Could not save '{fileName}'", Constants.WHITE, true));
                    }
                }

                TabControl.SelectedTab.AddImage(sourceFile.SubstringAfterLast('/'), false, bitmap, false, updateUi);
                return false;
            }
            case UAkAudioEvent when isNone && pointer.Object.Value is UAkAudioEvent { EventCookedData: { } wwiseData }:
            {
                foreach (var kvp in wwiseData.EventLanguageMap)
                {
                    if (!kvp.Value.HasValue) continue;

                    foreach (var media in kvp.Value.Value.Media)
                    {
                        if (!Provider.TrySaveAsset(Path.Combine("Game/WwiseAudio/", media.MediaPathName.Text),
                                out var data)) continue;

                        var namedPath = string.Concat(
                            Provider.ProjectName, "/Content/WwiseAudio/",
                            media.DebugName.Text.SubstringBeforeLast('.').Replace('\\', '/'),
                            " (", kvp.Key.LanguageName.Text, ")");
                        SaveAndPlaySound(namedPath, media.MediaPathName.Text.SubstringAfterLast('.'), data);
                    }
                }

                return false;
            }
            case UAkMediaAssetData when isNone:
            case USoundWave when isNone:
            {
                var shouldDecompress = UserSettings.Default.CompressedAudioMode == ECompressedAudio.PlayDecompressed;
                pointer.Object.Value.Decode(shouldDecompress, out var audioFormat, out var data);
                var hasAf = !string.IsNullOrEmpty(audioFormat);
                if (data == null || !hasAf)
                {
                    if (hasAf)
                        FLogger.Append(ELog.Warning,
                            () => FLogger.Text($"Unsupported audio format '{audioFormat}'", Constants.WHITE, true));
                    return false;
                }

                SaveAndPlaySound(TabControl.SelectedTab.Entry.PathWithoutExtension.Replace('\\', '/'), audioFormat,
                    data);
                return false;
            }
            case UWorld when isNone && UserSettings.Default.PreviewWorlds:
            case UBlueprintGeneratedClass when isNone && UserSettings.Default.PreviewWorlds &&
                                               TabControl.SelectedTab.ParentExportType switch
                                               {
                                                   "JunoBuildInstructionsItemDefinition" => true,
                                                   "JunoBuildingSetAccountItemDefinition" => true,
                                                   "JunoBuildingPropAccountItemDefinition" => true,
                                                   _ => false
                                               }:
            case UPaperSprite when isNone && UserSettings.Default.PreviewMaterials:
            case UStaticMesh when isNone && UserSettings.Default.PreviewStaticMeshes:
            case USkeletalMesh when isNone && UserSettings.Default.PreviewSkeletalMeshes:
            case USkeleton when isNone && UserSettings.Default.SaveSkeletonAsMesh:
            case UMaterialInstance when isNone && UserSettings.Default.PreviewMaterials &&
                                        !ModelIsOverwritingMaterial &&
                                        !(Provider.ProjectName.Equals("FortniteGame",
                                              StringComparison.OrdinalIgnoreCase) &&
                                          (pkg.Name.Contains("/MI_OfferImages/", StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/RenderSwitch_Materials/",
                                               StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/MI_BPTile/", StringComparison.OrdinalIgnoreCase))):
            {
                if (SnooperViewer.TryLoadExport(cancellationToken, dummy, pointer.Object))
                    SnooperViewer.Run();
                return true;
            }
            case UMaterialInstance
                when isNone && ModelIsOverwritingMaterial && pointer.Object.Value is UMaterialInstance m:
            {
                SnooperViewer.Renderer.Swap(m);
                SnooperViewer.Run();
                return true;
            }
            case UAnimSequenceBase when isNone && UserSettings.Default.PreviewAnimations ||
                                        SnooperViewer.Renderer.Options.ModelIsWaitingAnimation:
            {
                // animate all animations using their specified skeleton or when we explicitly asked for a loaded model to be animated (ignoring whether we wanted to preview animations)
                SnooperViewer.Renderer.Animate(pointer.Object.Value);
                SnooperViewer.Run();
                return true;
            }
            case UStaticMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeletalMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeleton when UserSettings.Default.SaveSkeletonAsMesh && HasFlag(bulk, EBulkType.Meshes):
            // case UMaterialInstance when HasFlag(bulk, EBulkType.Materials): // read the fucking json
            case UAnimSequenceBase when HasFlag(bulk, EBulkType.Animations):
            {
                SaveExport(pointer.Object.Value, updateUi);
                return true;
            }
            default:
            {
                if (!isNone && !saveTextures) return false;

                using var cPackage = new CreatorPackage(pkg.Name, dummy.ExportType, pointer.Object,
                    UserSettings.Default.CosmeticStyle);
                if (!cPackage.TryConstructCreator(out var creator))
                    return false;

                creator.ParseForInfo();
                TabControl.SelectedTab.AddImage(pointer.Object.Value.Name, false, creator.Draw(), saveTextures,
                    updateUi);
                return true;
            }
        }
    }

    public void ShowMetadata(GameFile entry)
    {
        var package = Provider.LoadPackage(entry);

        if (TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Metadata";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("");

        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(package, Formatting.Indented), false, false);
    }

    private void SaveAndPlaySound(string fullPath, string ext, byte[] data)
    {
        if (fullPath.StartsWith("/")) fullPath = fullPath[1..];
        var savedAudioPath = Path.Combine(UserSettings.Default.AudioDirectory,
                    UserSettings.Default.KeepDirectoryStructure ? fullPath : fullPath.SubstringAfterLast('/'))
                .Replace('\\', '/') + $".{ext.ToLowerInvariant()}";

        if (!UserSettings.Default.IsAutoOpenSounds)
        {
            Directory.CreateDirectory(savedAudioPath.SubstringBeforeLast('/'));
            using var stream = new FileStream(savedAudioPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(data);
            writer.Flush();
            return;
        }

        // TODO
        // since we are currently in a thread, the audio player's lifetime (memory-wise) will keep the current thread up and running until fmodel itself closes
        // the solution would be to kill the current thread at this line and then open the audio player without "Application.Current.Dispatcher.Invoke"
        // but the ThreadWorkerViewModel is an idiot and doesn't understand we want to kill the current thread inside the current thread and continue the code
        Application.Current.Dispatcher.Invoke(delegate
        {
            var audioPlayer = Helper.GetWindow<AudioPlayer>("Audio Player", () => new AudioPlayer().Show());
            audioPlayer.Load(data, savedAudioPath);
        });
    }

    private void SaveExport(UObject export, bool updateUi = true)
    {
        var toSave = new Exporter(export, UserSettings.Default.ExportOptions);
        var toSaveDirectory = new DirectoryInfo(UserSettings.Default.ModelDirectory);
        if (toSave.TryWriteToDir(toSaveDirectory, out var label, out var savedFilePath))
        {
            Log.Information("Successfully saved {FilePath}", savedFilePath);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully saved ", Constants.WHITE);
                    FLogger.Link(label, savedFilePath, true);
                });
            }
        }
        else
        {
            Log.Error("{FileName} could not be saved", export.Name);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{export.Name}'", Constants.WHITE, true));
        }
    }

    public void ConvertToCpp(CancellationToken cancellationToken, GameFile entry)
    {
        try
        {
            if (!entry.IsUePackage || (!entry.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase) &&
                                       !entry.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase)))
            {
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text($"File '{entry.Name}' is not a valid UE package for C++ conversion", Constants.WHITE,
                        true));
                return;
            }

            var cppCode = ProcessBlueprintToCpp(entry, cancellationToken);

            if (!string.IsNullOrEmpty(cppCode))
            {
                // Add new tab with the C++ code
                if (TabControl.CanAddTabs)
                    TabControl.AddTab($"{entry.NameWithoutExtension}.cpp");
                else
                    TabControl.SelectedTab.SoftReset(entry);

                TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("cpp");
                TabControl.SelectedTab.SetDocumentText(cppCode, false, true);
                TabControl.SelectedTab.TitleExtra = "C++ Conversion";

                FLogger.Append(ELog.Information, () =>
                    FLogger.Text($"Successfully converted '{entry.Name}' to C++", Constants.WHITE, true));
            }
            else
            {
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text($"No Blueprint or Verse class found in '{entry.Name}'", Constants.WHITE, true));
            }
        }
        catch (Exception ex)
        {
            FLogger.Append(ELog.Error, () =>
                FLogger.Text($"Error converting '{entry.Name}' to C++: {ex.Message}", Constants.WHITE, true));
            Log.Error("Error converting {EntryName} to C++: {Exception}", entry.Name, ex);
        }
    }

    public string ProcessBlueprintToCpp(GameFile package, CancellationToken cancellationToken)
    {
        // Ensure provider has script data enabled
        if (!Provider.ReadScriptData)
        {
            Console.WriteLine("Warning: ReadScriptData is disabled. Enabling it for bytecode access.");
            Provider.ReadScriptData = true;
            Provider.Initialize(); // Re-initialize if needed
        }

        var pkg = Provider.LoadPackage(package);
        var outputBuilder = new StringBuilder();
        var isVerse = false;

        for (var i = 0; i < pkg.ExportMapLength; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
            if (pointer?.Object is null) continue;

            var dummy = ((AbstractUePackage) pkg).ConstructObject(
                pointer.Class?.Object?.Value as UStruct, pkg);

            switch (dummy)
            {
                case UBlueprintGeneratedClass:
                case UVerseClass:
                    return ProcessBlueprintClass(pkg, dummy, out isVerse);
                default:
                    continue;
            }
        }

        return string.Empty;
    }


    private List<UFunction> GetOrderedFunctions(IPackage pkg, UBlueprintGeneratedClass blueprintClass,
        UVerseClass verseClass)
    {
        var funcMapOrder = blueprintClass?.FuncMap?.Keys.Select(fname => fname.ToString()).ToList() ??
                           verseClass?.FuncMap.Keys.Select(fname => fname.ToString()).ToList();

        return pkg.ExportsLazy
            .Where(e => e.Value is UFunction)
            .Select(e => (UFunction) e.Value)
            .OrderBy(f =>
            {
                if (funcMapOrder != null)
                {
                    var functionName = f.Name.ToString();
                    int index = funcMapOrder.IndexOf(functionName);
                    return index >= 0 ? index : int.MaxValue;
                }

                return int.MaxValue;
            })
            .ThenBy(f => f.Name.ToString())
            .ToList();
    }

    private Dictionary<string, List<int>> GenerateJumpCodeOffsetsMap(List<UFunction> functions)
    {
        var jumpCodeOffsetsMap = new Dictionary<string, List<int>>();

        // Process functions in reverse order (like standalone implementation)
        foreach (var function in functions.AsEnumerable().Reverse())
        {
            if (function?.ScriptBytecode == null)
                continue;

            foreach (var property in function.ScriptBytecode)
            {
                string label = null;
                int? offset = null;

                switch (property.Token)
                {
                    case EExprToken.EX_JumpIfNot:
                        label = ((EX_JumpIfNot) property).ObjectPath?.ToString()?.Split('.').Last().Split('[')[0];
                        offset = (int) ((EX_JumpIfNot) property).CodeOffset;
                        break;
                    case EExprToken.EX_Jump:
                        label = ((EX_Jump) property).ObjectPath?.ToString()?.Split('.').Last().Split('[')[0];
                        offset = (int) ((EX_Jump) property).CodeOffset;
                        break;
                    case EExprToken.EX_LocalFinalFunction:
                        EX_FinalFunction op = (EX_FinalFunction) property;
                        label = op.StackNode?.Name?.ToString()?.Split('.').Last().Split('[')[0];
                        if (op.Parameters.Length == 1 && op.Parameters[0] is EX_IntConst intConst)
                            offset = intConst.Value;
                        break;
                }

                if (!string.IsNullOrEmpty(label) && offset.HasValue)
                {
                    if (!jumpCodeOffsetsMap.TryGetValue(label, out var list))
                        jumpCodeOffsetsMap[label] = list = new List<int>();
                    list.Add(offset.Value);
                }
            }
        }

        return jumpCodeOffsetsMap;
    }

    private void ProcessFunctionsWithDiagnostics(List<UFunction> functions,
        Dictionary<string, List<int>> jumpCodeOffsetsMap, StringBuilder outputBuilder, bool isVerse)
    {
        int functionsWithBytecode = 0;
        int functionsWithoutBytecode = 0;

        foreach (var function in functions)
        {
            ProcessFunctionWithDiagnostics(function, jumpCodeOffsetsMap, outputBuilder, isVerse,
                ref functionsWithBytecode, ref functionsWithoutBytecode);
        }

        // Output summary
        Console.WriteLine(
            $"Processed {functions.Count} functions: {functionsWithBytecode} with bytecode, {functionsWithoutBytecode} without bytecode");
    }

    private void ProcessFunctionWithDiagnostics(UFunction function, Dictionary<string, List<int>> jumpCodeOffsetsMap,
        StringBuilder outputBuilder, bool isVerse, ref int withBytecode, ref int withoutBytecode)
    {
        string argsList = "";
        string returnFunc = "void";

        if (function?.ChildProperties != null)
        {
            foreach (FProperty property in function.ChildProperties)
            {
                if (property.Name.PlainText == "ReturnValue")
                {
                    returnFunc =
                        $"{(property.PropertyFlags.HasFlag(EPropertyFlags.ConstParm) ? "const " : string.Empty)}{GetPrefix(property.GetType().Name)}{GetPropertyType(property)}{(property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || GetPrefix(property.GetType().Name) == "U" ? "*" : string.Empty)}";
                }
                else if (ShouldIncludeParameter(property))
                {
                    argsList +=
                        $"{(property.PropertyFlags.HasFlag(EPropertyFlags.ConstParm) ? "const " : string.Empty)}{GetPrefix(property.GetType().Name)}{GetPropertyType(property)}{(property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || GetPrefix(property.GetType().Name) == "U" ? "*" : string.Empty)}{(property.PropertyFlags.HasFlag(EPropertyFlags.OutParm) ? "&" : string.Empty)} {Regex.Replace(property.Name.ToString(), @"^__verse_0x[0-9A-Fa-f]+_", "")}, ";
                }
            }
        }

        argsList = argsList.TrimEnd(',', ' ');

        outputBuilder.AppendLine($"\n\t{returnFunc} {function.Name.Replace(" ", "")}({argsList})");
        outputBuilder.AppendLine("\t{");

        if (function?.ScriptBytecode != null && function.ScriptBytecode.Length > 0)
        {
            withBytecode++;

            // Use pre-generated jump offsets or generate local ones
            var jumpCodeOffsets = jumpCodeOffsetsMap.TryGetValue(function.Name, out var list) ? list : new List<int>();

            // Process each bytecode instruction
            foreach (KismetExpression property in function.ScriptBytecode)
            {
                try
                {
                    ProcessExpression(property.Token, property, outputBuilder, jumpCodeOffsets);
                }
                catch (Exception ex)
                {
                    outputBuilder.AppendLine($"\t\t// Error processing {property.Token}: {ex.Message}");
                }
            }
        }
        else
        {
            withoutBytecode++;

            // Enhanced reason detection
            string reason = GetNoBytecodeReason(function);
            outputBuilder.AppendLine($"\t\t// {reason}");

            // Add some useful info for debugging
            if (function != null)
            {
                outputBuilder.AppendLine($"\t\t// Function flags: {function.FunctionFlags}");
                if (function.Super != null)
                {
                    outputBuilder.AppendLine($"\t\t// Super function: {function.Super.Name}");
                }
            }
        }

        outputBuilder.AppendLine("\t}");
    }

    private string GetNoBytecodeReason(UFunction function)
    {
        if (function == null)
            return "Function is null";

        if (function.FunctionFlags.HasFlag(EFunctionFlags.FUNC_Native))
            return "Native function - implemented in C++";

        if (function.FunctionFlags.HasFlag(EFunctionFlags.FUNC_BlueprintPure))
            return "Blueprint pure function - may not have script bytecode";

        if (function.FunctionFlags.HasFlag(EFunctionFlags.FUNC_BlueprintEvent))
            return "Blueprint event - may be interface or delegate";

        if (function.ScriptBytecode == null)
            return "ScriptBytecode is null - ensure ReadScriptData is enabled";

        if (function.ScriptBytecode.Length == 0)
            return "ScriptBytecode is empty - function may be abstract or placeholder";

        return "Function does not have bytecode for unknown reason";
    }

    private bool ShouldIncludeParameter(FProperty property)
    {
        var name = property.Name.ToString();
        return !(name.EndsWith("_ReturnValue") ||
                 name.StartsWith("CallFunc_") ||
                 name.StartsWith("K2Node_") ||
                 name.StartsWith("Temp_")) ||
               property.PropertyFlags.HasFlag(EPropertyFlags.Edit);
    }


    private void ProcessExpression(EExprToken token, KismetExpression expression, StringBuilder outputBuilder, List<int> jumpCodeOffsets, bool isParameter = false)
    {
        if (jumpCodeOffsets.Contains(expression.StatementIndex))
        {
            outputBuilder.Append("\t\tLabel_" + expression.StatementIndex + ":\n");
        }
        switch (token)
        {
            case EExprToken.EX_LetValueOnPersistentFrame:
                {
                    EX_LetValueOnPersistentFrame op = (EX_LetValueOnPersistentFrame) expression;
                    EX_VariableBase opp = (EX_VariableBase) op.AssignmentExpression;
                    var destination = ProcessTextProperty(op.DestinationProperty);
                    var variable = ProcessTextProperty(opp.Variable);

                    if (!isParameter)
                    {
                        outputBuilder.Append($"\t\t{(destination.Contains("K2Node_") ? $"UberGraphFrame->{destination}" : destination)} = {variable};\n\n"); // hardcoded but works
                    }
                    else
                    {
                        outputBuilder.Append($"\t\t{(destination.Contains("K2Node_") ? $"UberGraphFrame->{destination}" : destination)} = {variable}");
                    }
                    break;
                }
            case EExprToken.EX_LocalFinalFunction:
                {
                    EX_FinalFunction op = (EX_FinalFunction) expression;
                    KismetExpression[] opp = op.Parameters;
                    //Console.WriteLine(op.StackNode.Index);
                    //Console.WriteLine(op.StackNode.Name);
                    if (isParameter)
                    {
                        outputBuilder.Append($"{op.StackNode.Name.Replace(" ", "")}(");
                    }
                    else if (opp.Length < 1)
                    {
                        outputBuilder.Append($"\t\t{op?.StackNode?.Name.Replace(" ", "")}(");
                    }
                    else
                    {
                        outputBuilder.Append($"\t\t{GetPrefix(op?.StackNode?.ResolvedObject?.Outer?.GetType()?.Name ?? string.Empty)}{op?.StackNode?.Name.Replace(" ", "")}(");
                    }

                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t");
                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }
                    outputBuilder.Append(isParameter ? ")" : ");\n");
                    break;
                }
            case EExprToken.EX_FinalFunction:
                {
                    EX_FinalFunction op = (EX_FinalFunction) expression;
                    KismetExpression[] opp = op.Parameters;

                    // ignore this but I use it to Modify bytecode
                    //Console.WriteLine(op.StackNode.Index);
                    //Console.WriteLine(op.StackNode.Name);
                    //if (op.StackNode.Name == "CanRespawnOnStarterIsland")
                    //{
                    //Console.WriteLine("a");
                    //}
                    if (isParameter)
                    {
                        outputBuilder.Append($"{op.StackNode.Name.Replace(" ", "")}(");
                    }
                    else if (opp.Length < 1)
                    {
                        outputBuilder.Append($"\t\t{op?.StackNode?.Name.Replace(" ", "")}(");
                    }
                    else
                    {
                        outputBuilder.Append($"\t\t{op?.StackNode?.Name.Replace(" ", "")}(");//{Utils.GetPrefix(op?.StackNode?.ResolvedObject?.Outer?.GetType()?.Name)}
                    }

                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t");
                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }
                    outputBuilder.Append(isParameter ? ")" : ");\n\n");
                    break;
                }
            case EExprToken.EX_CallMath:
                {
                    EX_FinalFunction op = (EX_FinalFunction) expression;
                    KismetExpression[] opp = op.Parameters;
                    outputBuilder.Append(isParameter ? string.Empty : "\t\t");
                    outputBuilder.Append($"{GetPrefix(op.StackNode.ResolvedObject.Outer.GetType().Name)}{op.StackNode.ResolvedObject.Outer.Name.ToString().Replace(" ", "")}::{op.StackNode.Name}(");

                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t\t");
                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }
                    outputBuilder.Append(isParameter ? ")" : ");\n\n");
                    break;
                }
            case EExprToken.EX_LocalVirtualFunction:
            case EExprToken.EX_VirtualFunction:
                {
                    EX_VirtualFunction op = (EX_VirtualFunction) expression;
                    KismetExpression[] opp = op.Parameters;

                    //Console.WriteLine(op.VirtualFunctionName.Index);
                    //Console.WriteLine(op.VirtualFunctionName.PlainText);
                    if (isParameter)
                    {
                        outputBuilder.Append($"{op.VirtualFunctionName.PlainText.Replace(" ", "")}(");
                    }
                    else
                    {
                        outputBuilder.Append($"\t\t{op.VirtualFunctionName.PlainText.Replace(" ", "")}(");
                    }
                    for (int i = 0; i < opp.Length; i++)
                    {
                        if (opp.Length > 4)
                            outputBuilder.Append("\n\t\t");

                        ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                        if (i < opp.Length - 1)
                        {
                            outputBuilder.Append(", ");
                        }
                    }
                    outputBuilder.Append(isParameter ? ")" : ");\n\n");
                    break;
                }
            case EExprToken.EX_ComputedJump:
                {
                    EX_ComputedJump op = (EX_ComputedJump) expression;
                    if (op.CodeOffsetExpression is EX_VariableBase opp)
                    {
                        outputBuilder.AppendLine($"\t\tgoto {ProcessTextProperty(opp.Variable)};\n");
                    }
                    else if (op.CodeOffsetExpression is EX_CallMath oppMath)
                    {
                        ProcessExpression(oppMath.Token, oppMath, outputBuilder, jumpCodeOffsets, true);
                    }
                    else
                    {
                        Console.WriteLine("no idea how you reached this");
                    }
                    break;
                }
            case EExprToken.EX_PopExecutionFlowIfNot:
                {
                    EX_PopExecutionFlowIfNot op = (EX_PopExecutionFlowIfNot) expression;
                    outputBuilder.Append("\t\tif (!");
                    ProcessExpression(op.BooleanExpression.Token, op.BooleanExpression, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(") \r\n");
                    outputBuilder.Append($"\t\t    FlowStack.Pop();\n\n");
                    break;
                }
            case EExprToken.EX_Cast:
                {
                    EX_Cast op = (EX_Cast) expression;// support CST_ObjectToInterface when I have an example of how it works

                    if (ECastToken.CST_ObjectToBool == op.ConversionType || ECastToken.CST_InterfaceToBool == op.ConversionType)
                    {
                        outputBuilder.Append("(bool)");
                    }
                    if (ECastToken.CST_DoubleToFloat == op.ConversionType)
                    {
                        outputBuilder.Append("(float)");
                    }
                    if (ECastToken.CST_FloatToDouble == op.ConversionType)
                    {
                        outputBuilder.Append("(double)");
                    }
                    ProcessExpression(op.Target.Token, op.Target, outputBuilder, jumpCodeOffsets);
                    break;
                }
            case EExprToken.EX_InterfaceContext:
                {
                    EX_InterfaceContext op = (EX_InterfaceContext) expression;
                    ProcessExpression(op.InterfaceValue.Token, op.InterfaceValue, outputBuilder, jumpCodeOffsets);
                    break;
                }
            case EExprToken.EX_ArrayConst:
                {
                    EX_ArrayConst op = (EX_ArrayConst) expression;
                    outputBuilder.Append("TArray {");
                    foreach (KismetExpression element in op.Elements)
                    {
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);
                    }
                    outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                    outputBuilder.Append("}");
                    break;
                }
            case EExprToken.EX_SetArray:
                {
                    EX_SetArray op = (EX_SetArray) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.AssigningProperty.Token, op.AssigningProperty, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(" = ");
                    outputBuilder.Append("TArray {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        KismetExpression element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);

                        outputBuilder.Append(i < op.Elements.Length - 1 ? "," : "");
                    }

                    outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                    outputBuilder.Append("};\n\n");
                    break;
                }
            case EExprToken.EX_SetSet:
                {
                    EX_SetSet op = (EX_SetSet) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.SetProperty.Token, op.SetProperty, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(" = ");
                    outputBuilder.Append("TArray {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        KismetExpression element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);

                        outputBuilder.Append(i < op.Elements.Length - 1 ? "," : "");
                    }

                    outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                    outputBuilder.Append("};\n\n");
                    break;
                }
            case EExprToken.EX_SetConst:
                {
                    EX_SetConst op = (EX_SetConst) expression;
                    outputBuilder.Append("TArray {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        KismetExpression element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets, true);

                        outputBuilder.Append(i < op.Elements.Length - 1 ? "," : "");
                    }

                    outputBuilder.Append(op.Elements.Length < 1 ? "  " : ' ');

                    outputBuilder.Append("};\n\n");
                    break;
                }
            case EExprToken.EX_SetMap:
                {
                    EX_SetMap op = (EX_SetMap) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.MapProperty.Token, op.MapProperty, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append(" = ");
                    outputBuilder.Append("TMap {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        var element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets);// sometimes the start of an array is a byte not a variable

                        if (i < op.Elements.Length - 1)
                        {
                            outputBuilder.Append(element.Token == EExprToken.EX_InstanceVariable ? ": " : ", ");
                        }
                        else
                        {
                            outputBuilder.Append(' ');
                        }
                    }

                    if (op.Elements.Length < 1)
                        outputBuilder.Append("  ");
                    outputBuilder.Append("}\n");
                    break;
                }
            case EExprToken.EX_MapConst:
                {
                    EX_MapConst op = (EX_MapConst) expression;
                    outputBuilder.Append("TMap {");
                    for (int i = 0; i < op.Elements.Length; i++)
                    {
                        var element = op.Elements[i];
                        outputBuilder.Append(' ');
                        ProcessExpression(element.Token, element, outputBuilder, jumpCodeOffsets, true);// sometimes the start of an array is a byte not a variable

                        if (i < op.Elements.Length - 1)
                        {
                            outputBuilder.Append(element.Token == EExprToken.EX_InstanceVariable ? ": " : ", ");
                        }
                        else
                        {
                            outputBuilder.Append(' ');
                        }
                    }

                    if (op.Elements.Length < 1)
                        outputBuilder.Append("  ");
                    outputBuilder.Append("}\n");
                    break;
                }
            case EExprToken.EX_SwitchValue:
                {
                    EX_SwitchValue op = (EX_SwitchValue) expression;

                    bool useTernary = op.Cases.Length <= 2
                        && op.Cases.All(c => c.CaseIndexValueTerm.Token == EExprToken.EX_True || c.CaseIndexValueTerm.Token == EExprToken.EX_False);

                    if (useTernary)
                    {
                        ProcessExpression(op.IndexTerm.Token, op.IndexTerm, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(" ? ");

                        bool isFirst = true;
                        foreach (var caseItem in op.Cases.Where(c => c.CaseIndexValueTerm.Token == EExprToken.EX_True))
                        {
                            if (!isFirst)
                                outputBuilder.Append(" : ");

                            ProcessExpression(caseItem.CaseTerm.Token, caseItem.CaseTerm, outputBuilder, jumpCodeOffsets, true);
                            isFirst = false;
                        }

                        foreach (var caseItem in op.Cases.Where(c => c.CaseIndexValueTerm.Token == EExprToken.EX_False))
                        {
                            if (!isFirst)
                                outputBuilder.Append(" : ");

                            ProcessExpression(caseItem.CaseTerm.Token, caseItem.CaseTerm, outputBuilder, jumpCodeOffsets, true);
                        }
                    }
                    else
                    {
                        outputBuilder.Append("switch (");
                        ProcessExpression(op.IndexTerm.Token, op.IndexTerm, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(")\n");
                        outputBuilder.Append("\t\t{\n");

                        foreach (var caseItem in op.Cases)
                        {
                            if (caseItem.CaseIndexValueTerm.Token == EExprToken.EX_IntConst)
                            {
                                int caseValue = ((EX_IntConst) caseItem.CaseIndexValueTerm).Value;
                                outputBuilder.Append($"\t\t\tcase {caseValue}:\n");
                            }
                            else
                            {
                                outputBuilder.Append("\t\t\tcase ");
                                ProcessExpression(caseItem.CaseIndexValueTerm.Token, caseItem.CaseIndexValueTerm, outputBuilder, jumpCodeOffsets);
                                outputBuilder.Append(":\n");
                            }

                            outputBuilder.Append("\t\t\t{\n");
                            outputBuilder.Append("\t\t\t    ");
                            ProcessExpression(caseItem.CaseTerm.Token, caseItem.CaseTerm, outputBuilder, jumpCodeOffsets);
                            outputBuilder.Append(";\n");
                            outputBuilder.Append("\t\t\t    break;\n");
                            outputBuilder.Append("\t\t\t}\n");
                        }

                        outputBuilder.Append("\t\t\tdefault:\n");
                        outputBuilder.Append("\t\t\t{\n");
                        outputBuilder.Append("\t\t\t    ");
                        ProcessExpression(op.DefaultTerm.Token, op.DefaultTerm, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append("\n\t\t\t}\n");

                        outputBuilder.Append("\t\t}");
                    }
                    break;
                }
            case EExprToken.EX_ArrayGetByRef: // I assume get array with index
                {
                    EX_ArrayGetByRef op = (EX_ArrayGetByRef) expression; // FortniteGame/Plugins/GameFeatures/FM/PilgrimCore/Content/Player/Components/BP_PilgrimPlayerControllerComponent.uasset
                    ProcessExpression(op.ArrayVariable.Token, op.ArrayVariable, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append("[");
                    ProcessExpression(op.ArrayIndex.Token, op.ArrayIndex, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append("]");
                    break;
                }
            case EExprToken.EX_MetaCast:
            case EExprToken.EX_DynamicCast:
            case EExprToken.EX_ObjToInterfaceCast:
            case EExprToken.EX_CrossInterfaceCast:
            case EExprToken.EX_InterfaceToObjCast:
                {
                    EX_CastBase op = (EX_CastBase) expression;
                    outputBuilder.Append($"Cast<U{op.ClassPtr.Name}*>(");// m?
                    ProcessExpression(op.Target.Token, op.Target, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(")");
                    break;
                }
            case EExprToken.EX_StructConst:
                {
                    EX_StructConst op = (EX_StructConst) expression;
                    outputBuilder.Append($"{GetPrefix(op.Struct.GetType().Name)}{op.Struct.Name}");
                    outputBuilder.Append($"(");
                    for (int i = 0; i < op.Properties.Length; i++)
                    {
                        var property = op.Properties[i];
                        ProcessExpression(property.Token, property, outputBuilder, jumpCodeOffsets);
                        if (i < op.Properties.Length - 1 && property.Token != EExprToken.EX_ArrayConst)
                            outputBuilder.Append(", ");
                    }
                    outputBuilder.Append($")");
                    break;
                }
            case EExprToken.EX_ObjectConst:
                {
                    EX_ObjectConst op = (EX_ObjectConst) expression;
                    outputBuilder.Append(!isParameter ? "\t\tFindObject<" : outputBuilder.ToString().EndsWith("\n") ? "\t\tFindObject<" : "FindObject<"); // please don't complain, i know this is bad but i MUST do it.
                    string classString = op?.Value?.ResolvedObject?.Class?.ToString()?.Replace("'", "");

                    if (classString?.Contains(".") == true)
                    {

                        outputBuilder.Append(GetPrefix(op?.Value?.ResolvedObject?.Class?.GetType().Name) + classString.Split(".")[1]);
                    }
                    else
                    {
                        outputBuilder.Append(GetPrefix(op?.Value?.ResolvedObject?.Class?.GetType().Name) + classString);
                    }
                    outputBuilder.Append(">(\"");
                    var resolvedObject = op?.Value?.ResolvedObject;
                    var outerString = resolvedObject?.Outer?.ToString()?.Replace("'", "") ?? "UNKNOWN";
                    var outerClassString = resolvedObject?.Class?.ToString()?.Replace("'", "") ?? "UNKNOWN";
                    var name = op?.Value?.Name ?? string.Empty;

                    outputBuilder.Append(outerString.Replace(outerClassString, "") + "." + name);

                    if (isParameter)
                    {
                        outputBuilder.Append("\")");
                    }
                    else
                    {
                        outputBuilder.Append("\")");
                    }
                    break;
                }
            case EExprToken.EX_BindDelegate:
                {
                    EX_BindDelegate op = (EX_BindDelegate) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append($".BindUFunction(");
                    ProcessExpression(op.ObjectTerm.Token, op.ObjectTerm, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append($", \"{op.FunctionName}\"");
                    outputBuilder.Append($");\n\n");
                    break;
                }
            // all the delegate functions suck
            case EExprToken.EX_AddMulticastDelegate:
                {
                    EX_AddMulticastDelegate op = (EX_AddMulticastDelegate) expression;
                    if (op.Delegate.Token == EExprToken.EX_LocalVariable || op.Delegate.Token == EExprToken.EX_InstanceVariable)
                    {
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append(".AddDelegate(");
                        ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append($");\n\n");
                    }
                    else if (op.Delegate.Token != EExprToken.EX_Context)
                    {
                        Console.WriteLine($"Issue: EX_AddMulticastDelegate missing info: {op.StatementIndex}, {op.Delegate.Token}");
                    }
                    else
                    {
                        //EX_Context opp = (EX_Context) op.Delegate;
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        //outputBuilder.Append("->");
                        //ProcessExpression(opp.ContextExpression.Token, opp.ContextExpression, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(".AddDelegate(");
                        ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append($");\n\n");
                    }
                    break;
                }
            case EExprToken.EX_RemoveMulticastDelegate: // everything here has been guessed not compared to actual UE but does work fine and displays all information
                {
                    EX_RemoveMulticastDelegate op = (EX_RemoveMulticastDelegate) expression;
                    if (op.Delegate.Token == EExprToken.EX_LocalVariable || op.Delegate.Token == EExprToken.EX_InstanceVariable)
                    {
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append(".RemoveDelegate(");
                        ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append($");\n\n");
                    }
                    else if (op.Delegate.Token != EExprToken.EX_Context)
                    {
                        Console.WriteLine("Issue: EX_RemoveMulticastDelegate missing info: {0}", op.StatementIndex);
                    }
                    else
                    {
                        EX_Context opp = (EX_Context) op.Delegate;
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append("->");
                        ProcessExpression(opp.ContextExpression.Token, opp.ContextExpression, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append(".RemoveDelegate(");
                        ProcessExpression(op.DelegateToAdd.Token, op.DelegateToAdd, outputBuilder, jumpCodeOffsets);
                        outputBuilder.Append($");\n\n");
                    }
                    break;
                }
            case EExprToken.EX_ClearMulticastDelegate: // this also
                {
                    EX_ClearMulticastDelegate op = (EX_ClearMulticastDelegate) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.DelegateToClear.Token, op.DelegateToClear, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(".Clear();\n\n");
                    break;
                }
            case EExprToken.EX_CallMulticastDelegate: // this also
                {
                    EX_CallMulticastDelegate op = (EX_CallMulticastDelegate) expression;
                    KismetExpression[] opp = op.Parameters;
                    if (op.Delegate.Token == EExprToken.EX_LocalVariable || op.Delegate.Token == EExprToken.EX_InstanceVariable)
                    {
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append(".Call(");
                        for (int i = 0; i < opp.Length; i++)
                        {
                            if (opp.Length > 4)
                                outputBuilder.Append("\n\t\t");
                            ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                            if (i < opp.Length - 1)
                            {
                                outputBuilder.Append(", ");
                            }
                        }
                        outputBuilder.Append($");\n\n");
                    }
                    else if (op.Delegate.Token != EExprToken.EX_Context)
                    {
                        Console.WriteLine("Issue: EX_CallMulticastDelegate missing info: {0}", op.StatementIndex);
                    }
                    else
                    {
                        outputBuilder.Append("\t\t");
                        ProcessExpression(op.Delegate.Token, op.Delegate, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append(".Call(");
                        for (int i = 0; i < opp.Length; i++)
                        {
                            if (opp.Length > 4)
                                outputBuilder.Append("\n\t\t");
                            ProcessExpression(opp[i].Token, opp[i], outputBuilder, jumpCodeOffsets, true);
                            if (i < opp.Length - 1)
                            {
                                outputBuilder.Append(", ");
                            }
                        }
                        outputBuilder.Append($");\n\n");
                    }
                    break;
                }
            case EExprToken.EX_ClassContext:
            case EExprToken.EX_Context:
                {
                    EX_Context op = (EX_Context) expression;
                    outputBuilder.Append(outputBuilder.ToString().EndsWith("\n") ? "\t\t" : "");
                    ProcessExpression(op.ObjectExpression.Token, op.ObjectExpression, outputBuilder, jumpCodeOffsets, true);

                    outputBuilder.Append("->");
                    ProcessExpression(op.ContextExpression.Token, op.ContextExpression, outputBuilder, jumpCodeOffsets, true);
                    if (!isParameter)
                    {
                        outputBuilder.Append(";\n\n");
                    }
                    break;
                }
            case EExprToken.EX_Context_FailSilent:
                {
                    EX_Context op = (EX_Context) expression;
                    outputBuilder.Append("\t\t");
                    ProcessExpression(op.ObjectExpression.Token, op.ObjectExpression, outputBuilder, jumpCodeOffsets, true);
                    if (!isParameter)
                    {
                        outputBuilder.Append("->");
                        ProcessExpression(op.ContextExpression.Token, op.ContextExpression, outputBuilder, jumpCodeOffsets, true);
                        outputBuilder.Append($";\n\n");
                    }
                    break;
                }
            case EExprToken.EX_Let:
                {
                    EX_Let op = (EX_Let) expression;
                    if (!isParameter)
                    {
                        outputBuilder.Append("\t\t");
                    }
                    ProcessExpression(op.Variable.Token, op.Variable, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(" = ");
                    ProcessExpression(op.Assignment.Token, op.Assignment, outputBuilder, jumpCodeOffsets, true);
                    if (!isParameter)
                    {
                        outputBuilder.Append(";\n\n");
                    }
                    break;
                }
            case EExprToken.EX_LetObj:
            case EExprToken.EX_LetWeakObjPtr:
            case EExprToken.EX_LetBool:
            case EExprToken.EX_LetDelegate:
            case EExprToken.EX_LetMulticastDelegate:
                {
                    EX_LetBase op = (EX_LetBase) expression;
                    if (!isParameter)
                    {
                        outputBuilder.Append("\t\t");
                    }
                    ProcessExpression(op.Variable.Token, op.Variable, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(" = ");
                    ProcessExpression(op.Assignment.Token, op.Assignment, outputBuilder, jumpCodeOffsets, true);
                    if (!isParameter || op.Assignment.Token == EExprToken.EX_LocalFinalFunction || op.Assignment.Token == EExprToken.EX_FinalFunction || op.Assignment.Token == EExprToken.EX_CallMath)
                    {
                        outputBuilder.Append($";\n\n");
                    }
                    else
                    {
                        outputBuilder.Append($";");
                    }
                    break;
                }
            case EExprToken.EX_JumpIfNot:
                {
                    EX_JumpIfNot op = (EX_JumpIfNot) expression;
                    outputBuilder.Append("\t\tif (!");
                    ProcessExpression(op.BooleanExpression.Token, op.BooleanExpression, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.Append(") \r\n");
                    outputBuilder.Append("\t\t    goto Label_");
                    outputBuilder.Append(op.CodeOffset);
                    outputBuilder.Append(";\n\n");
                    break;
                }
            case EExprToken.EX_Jump:
                {
                    EX_Jump op = (EX_Jump) expression;
                    outputBuilder.Append($"\t\tgoto Label_{op.CodeOffset};\n\n");
                    break;
                }
            // Static expressions

            case EExprToken.EX_TextConst:
                {
                    EX_TextConst op = (EX_TextConst) expression;

                    if (op.Value is FScriptText scriptText)
                    {
                        if (scriptText.SourceString == null)
                        {
                            outputBuilder.Append("nullptr");
                        }
                        else
                            ProcessExpression(scriptText.SourceString.Token, scriptText.SourceString, outputBuilder, jumpCodeOffsets, true);
                    }
                    else
                    {
                        outputBuilder.Append(op.Value);
                    }
                }
                break;
            case EExprToken.EX_StructMemberContext:
                {
                    EX_StructMemberContext op = (EX_StructMemberContext) expression;
                    ProcessExpression(op.StructExpression.Token, op.StructExpression, outputBuilder, jumpCodeOffsets);
                    outputBuilder.Append('.');
                    outputBuilder.Append(ProcessTextProperty(op.Property));
                    break;
                }
            case EExprToken.EX_Return:
                {
                    EX_Return op = (EX_Return) expression;
                    bool check = op.ReturnExpression.Token == EExprToken.EX_Nothing;
                    outputBuilder.Append($"\t\treturn");
                    if (!check)
                        outputBuilder.Append(' ');
                    ProcessExpression(op.ReturnExpression.Token, op.ReturnExpression, outputBuilder, jumpCodeOffsets, true);
                    outputBuilder.AppendLine(";\n\n");
                    break;
                }
            case EExprToken.EX_RotationConst:
                {
                    EX_RotationConst op = (EX_RotationConst) expression;
                    FRotator value = op.Value;
                    outputBuilder.Append($"FRotator({value.Pitch}, {value.Yaw}, {value.Roll})");
                    break;
                }
            case EExprToken.EX_VectorConst:
                {
                    EX_VectorConst op = (EX_VectorConst) expression;
                    FVector value = op.Value;
                    outputBuilder.Append($"FVector({value.X}, {value.Y}, {value.Z})");
                    break;
                }
            case EExprToken.EX_Vector3fConst:
                {
                    EX_Vector3fConst op = (EX_Vector3fConst) expression;
                    FVector value = op.Value;
                    outputBuilder.Append($"FVector3f({value.X}, {value.Y}, {value.Z})");
                    break;
                }
            case EExprToken.EX_TransformConst:
                {
                    EX_TransformConst op = (EX_TransformConst) expression;
                    FTransform value = op.Value;
                    outputBuilder.Append($"FTransform(FQuat({value.Rotation.X}, {value.Rotation.Y}, {value.Rotation.Z}, {value.Rotation.W}), FVector({value.Translation.X}, {value.Translation.Y}, {value.Translation.Z}), FVector({value.Scale3D.X}, {value.Scale3D.Y}, {value.Scale3D.Z}))");
                    break;
                }


            case EExprToken.EX_LocalVariable:
            case EExprToken.EX_DefaultVariable:
            case EExprToken.EX_InstanceVariable:
            case EExprToken.EX_LocalOutVariable:
            case EExprToken.EX_ClassSparseDataVariable:
                outputBuilder.Append(ProcessTextProperty(((EX_VariableBase) expression).Variable));
                break;

            case EExprToken.EX_ByteConst:
            case EExprToken.EX_IntConstByte:
                outputBuilder.Append($"0x{((KismetExpression<byte>) expression).Value.ToString("X")}");
                break;
            case EExprToken.EX_SoftObjectConst:
                ProcessExpression(((EX_SoftObjectConst) expression).Value.Token, ((EX_SoftObjectConst) expression).Value, outputBuilder, jumpCodeOffsets);
                break;
            case EExprToken.EX_DoubleConst:
                {
                    double value = ((EX_DoubleConst) expression).Value;
                    outputBuilder.Append(Math.Abs(value - Math.Floor(value)) < 1e-10 ? (int) value : value.ToString("R"));
                    break;
                }
            case EExprToken.EX_NameConst:
                outputBuilder.Append($"\"{((EX_NameConst) expression).Value}\"");
                break;
            case EExprToken.EX_IntConst:
                outputBuilder.Append(((EX_IntConst) expression).Value.ToString());
                break;
            case EExprToken.EX_PropertyConst:
                outputBuilder.Append(ProcessTextProperty(((EX_PropertyConst) expression).Property));
                break;
            case EExprToken.EX_StringConst:
                outputBuilder.Append($"\"{((EX_StringConst) expression).Value}\"");
                break;
            case EExprToken.EX_FieldPathConst:
                ProcessExpression(((EX_FieldPathConst) expression).Value.Token, ((EX_FieldPathConst) expression).Value, outputBuilder, jumpCodeOffsets);
                break;
            case EExprToken.EX_Int64Const:
                outputBuilder.Append(((EX_Int64Const) expression).Value.ToString());
                break;
            case EExprToken.EX_UInt64Const:
                outputBuilder.Append(((EX_UInt64Const) expression).Value.ToString());
                break;
            case EExprToken.EX_SkipOffsetConst:
                outputBuilder.Append(((EX_SkipOffsetConst) expression).Value.ToString());
                break;
            case EExprToken.EX_FloatConst:
                outputBuilder.Append(((EX_FloatConst) expression).Value.ToString(CultureInfo.GetCultureInfo("en-US")));
                break;
            case EExprToken.EX_BitFieldConst:
                outputBuilder.Append(((EX_BitFieldConst) expression).ConstValue);
                break;
            case EExprToken.EX_UnicodeStringConst:
                outputBuilder.Append(((EX_UnicodeStringConst) expression).Value);
                break;
            case EExprToken.EX_InstanceDelegate:
                outputBuilder.Append($"\"{((EX_InstanceDelegate) expression).FunctionName}\"");
                break;
            case EExprToken.EX_EndOfScript:
            case EExprToken.EX_EndParmValue:
                outputBuilder.Append("\t}\n");
                break;
            case EExprToken.EX_NoObject:
            case EExprToken.EX_NoInterface:
                outputBuilder.Append("nullptr");
                break;
            case EExprToken.EX_IntOne:
                outputBuilder.Append(1);
                break;
            case EExprToken.EX_IntZero:
                outputBuilder.Append(0);
                break;
            case EExprToken.EX_True:
                outputBuilder.Append("true");
                break;
            case EExprToken.EX_False:
                outputBuilder.Append("false");
                break;
            case EExprToken.EX_Self:
                outputBuilder.Append("this");
                break;

            case EExprToken.EX_Nothing:
            case EExprToken.EX_NothingInt32:
            case EExprToken.EX_EndFunctionParms:
            case EExprToken.EX_EndStructConst:
            case EExprToken.EX_EndArray:
            case EExprToken.EX_EndArrayConst:
            case EExprToken.EX_EndSet:
            case EExprToken.EX_EndMap:
            case EExprToken.EX_EndMapConst:
            case EExprToken.EX_EndSetConst:
            case EExprToken.EX_PushExecutionFlow:
            case EExprToken.EX_PopExecutionFlow:
            case EExprToken.EX_DeprecatedOp4A:
            case EExprToken.EX_WireTracepoint:
            case EExprToken.EX_Tracepoint:
            case EExprToken.EX_Breakpoint:
            case EExprToken.EX_AutoRtfmStopTransact:
            case EExprToken.EX_AutoRtfmTransact:
            case EExprToken.EX_AutoRtfmAbortIfNot:
                // some here are "useful" and unsupported
                break;
            /*
            EExprToken.EX_Assert
            EExprToken.EX_Skip
            EExprToken.EX_InstrumentationEvent
            EExprToken.EX_FieldPathConst
            */
            default:
                Console.WriteLine($"Error: Unknown bytecode {token}");
                outputBuilder.Append($"{token}");
                break;
        }
    }

    private string ProcessBlueprintClass(IPackage pkg, UObject dummy, out bool isVerse)
    {
        var outputBuilder = new StringBuilder();
        isVerse = false;

        var blueprintGeneratedClass =
            pkg.ExportsLazy.FirstOrDefault(e => e.Value is UBlueprintGeneratedClass)?.Value as UBlueprintGeneratedClass;
        var verseClass = pkg.ExportsLazy.Where(export => export.Value is UVerseClass)
            .Select(export => (UVerseClass) export.Value).FirstOrDefault();

        if (verseClass != null)
        {
            isVerse = true;
            _isVerse = true;
        }

        if (blueprintGeneratedClass == null && !isVerse)
            return string.Empty;

        var mainClass = blueprintGeneratedClass?.Name ?? verseClass?.Name;
        var superStructName =
            blueprintGeneratedClass?.SuperStruct?.Name ?? verseClass?.SuperStruct?.Name ?? string.Empty;

        outputBuilder.AppendLine(
            $"class {GetPrefix(blueprintGeneratedClass?.GetType().Name ?? verseClass?.GetType().Name)}{mainClass} : public {GetPrefix(blueprintGeneratedClass?.GetType().Name ?? verseClass?.GetType().Name)}{superStructName}");
        outputBuilder.AppendLine("{");
        outputBuilder.AppendLine("public:");

        // Process properties from default objects
        var stringsArray = ProcessDefaultObjectProperties(pkg, mainClass, outputBuilder, isVerse);

        // Process child properties
        ProcessChildProperties(blueprintGeneratedClass?.ChildProperties ?? verseClass?.ChildProperties, stringsArray,
            outputBuilder);

        // Get functions with better ordering and filtering
        var functions = GetOrderedFunctions(pkg, blueprintGeneratedClass, verseClass);

        // Pre-generate jump code offsets for all functions (like standalone implementation)
        var jumpCodeOffsetsMap = GenerateJumpCodeOffsetsMap(functions);

        // Process functions with enhanced diagnostics
        ProcessFunctionsWithDiagnostics(functions, jumpCodeOffsetsMap, outputBuilder, isVerse);

        outputBuilder.AppendLine();
        outputBuilder.AppendLine("};");

        // Replace placeholders
        string pattern = @"\w+placenolder";
        return Regex.Replace(outputBuilder.ToString(), pattern, "nullptr");
    }

    private List<string> ProcessDefaultObjectProperties(IPackage pkg, string mainClass, StringBuilder outputBuilder,
        bool isVerse)
    {
        var stringsArray = new List<string>();

        foreach (var export in pkg.ExportsLazy)
        {
            if (export.Value is not UBlueprintGeneratedClass &&
                export.Value.Name.StartsWith("Default__") &&
                export.Value.Name.EndsWith(mainClass ?? string.Empty))
            {
                var exportObject = export.Value;
                foreach (var key in exportObject.Properties)
                {
                    stringsArray.Add(key.Name.PlainText);
                    ProcessProperty(key, outputBuilder, isVerse);
                }
            }
        }

        return stringsArray;
    }

    private void ProcessProperty(FPropertyTag key, StringBuilder outputBuilder, bool isVerse)
    {
        string placeholder = $"{key.Name}placenolder";
        string result = key.Tag.GenericValue?.ToString() ?? string.Empty;
        string keyName = key.Name.PlainText.Replace(" ", "");
        var propertyTag = key.Tag.GetValue(typeof(object));

        void ShouldAppend(string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            if (outputBuilder.ToString().Contains(placeholder))
            {
                outputBuilder.Replace(placeholder, value);
            }
            else
            {
                outputBuilder.AppendLine($"\t{GetPropertyType(propertyTag)} {keyName} = {value};");
            }
        }

        // Handle different property types
        switch (key.Tag.GenericValue)
        {
            case FScriptStruct structTag:
                ProcessStructProperty(structTag, ShouldAppend);
                break;
            case UScriptSet set:
                ProcessSetProperty(set, ShouldAppend);
                break;
            case UScriptMap map:
                ProcessMapProperty(map, ShouldAppend);
                break;
            case UScriptArray array:
                ProcessArrayProperty(array, ShouldAppend);
                break;
            case bool boolResult:
                ShouldAppend(boolResult.ToString().ToLower());
                break;
            default:
                if (IsStringLikeProperty(key))
                    ShouldAppend($"\"{result}\"");
                else
                    ShouldAppend(result);
                break;
        }
    }

    private void ProcessStructProperty(FScriptStruct structTag, Action<string> shouldAppend)
    {
        switch (structTag.StructType)
        {
            case FVector vector:
                shouldAppend($"FVector({vector.X}, {vector.Y}, {vector.Z})");
                break;
            case FVector2D vector2d:
                shouldAppend($"FVector2D({vector2d.X}, {vector2d.Y})");
                break;
            case FRotator rotator:
                shouldAppend($"FRotator({rotator.Pitch}, {rotator.Yaw}, {rotator.Roll})");
                break;
            case FLinearColor color:
                shouldAppend($"FLinearColor({color.R}, {color.G}, {color.B}, {color.A})");
                break;
            case FGuid guid:
                shouldAppend($"FGuid({guid.A}, {guid.B}, {guid.C}, {guid.D})");
                break;
            case FGameplayTagContainer gameplayTag:
                ProcessGameplayTags(gameplayTag, shouldAppend);
                break;
            case FStructFallback fallback:
                ProcessStructFallback(fallback, shouldAppend);
                break;
            default:
                shouldAppend($"\"{structTag.StructType}\"");
                break;
        }
    }

    private void ProcessGameplayTags(FGameplayTagContainer gameplayTag, Action<string> shouldAppend)
    {
        var tags = gameplayTag.GameplayTags.ToList();
        if (tags.Count > 1)
        {
            var formattedTags = "[\n" + string.Join(",\n", tags.Select(tag => $"\t\t\"{tag.TagName}\"")) + "\n\t]";
            shouldAppend(formattedTags);
        }
        else if (tags.Any())
        {
            shouldAppend($"\"{tags.First().TagName}\"");
        }
        else
        {
            shouldAppend("[]");
        }
    }

    private void ProcessStructFallback(FStructFallback fallback, Action<string> shouldAppend)
    {
        if (fallback.Properties.Count > 0)
        {
            var formattedTags = "[\n" + string.Join(",\n",
                fallback.Properties.Select(tag =>
                {
                    string tagDataFormatted = tag.Tag switch
                    {
                        TextProperty text => $"\"{text.Value.Text}\"",
                        NameProperty name => $"\"{name.Value.Text}\"",
                        ObjectProperty objectProperty => $"\"{objectProperty.Value}\"",
                        _ => tag.Tag.GenericValue?.ToString() ?? "{}"
                    };
                    return $"\t\t{{ \"{tag.Name}\": {tagDataFormatted} }}";
                })) + "\n\t]";
            shouldAppend(formattedTags);
        }
        else
        {
            shouldAppend("[]");
        }
    }

    private void ProcessSetProperty(UScriptSet set, Action<string> shouldAppend)
    {
        var formattedSet = "[\n" + string.Join(",\n", set.Properties.Select(p => $"\t\"{p.GenericValue}\"")) + "\n\t]";
        shouldAppend(formattedSet);
    }

    private void ProcessMapProperty(UScriptMap map, Action<string> shouldAppend)
    {
        var formattedMap = "[\n" +
                           string.Join(",\n",
                               map.Properties.Select(kvp => $"\t{{\n\t\t\"{kvp.Key}\": \"{kvp.Value}\"\n\t}}")) +
                           "\n\t]";
        shouldAppend(formattedMap);
    }

    private void ProcessArrayProperty(UScriptArray array, Action<string> shouldAppend)
    {
        var formattedArray = "[\n" + string.Join(",\n", array.Properties.Select(p =>
        {
            if (p.GenericValue is FScriptStruct vectorInArray && vectorInArray.StructType is FVector vector)
            {
                return $"FVector({vector.X}, {vector.Y}, {vector.Z})";
            }

            if (p.GenericValue is FScriptStruct vector2dInArray && vector2dInArray.StructType is FVector2D vector2d)
            {
                return $"FVector2D({vector2d.X}, {vector2d.Y})";
            }

            if (p.GenericValue is FScriptStruct structInArray && structInArray.StructType is FRotator rotator)
            {
                return $"FRotator({rotator.Pitch}, {rotator.Yaw}, {rotator.Roll})";
            }
            else if (p.GenericValue is FScriptStruct fallbacksInArray &&
                     fallbacksInArray.StructType is FStructFallback fallback)
            {
                if (fallback.Properties.Count > 0)
                {
                    var formattedTags = "\t[\n" + string.Join(",\n",
                        fallback.Properties.Select(tag =>
                        {
                            string tagDataFormatted = tag.Tag switch
                            {
                                TextProperty text => $"\"{text.Value.Text}\"",
                                NameProperty name => $"\"{name.Value.Text}\"",
                                ObjectProperty objectProperty => $"\"{objectProperty.Value}\"",
                                _ => $"\"{tag.Tag.GenericValue}\""
                            };
                            return $"\t\t\"{tag.Name}\": {tagDataFormatted}";
                        })) + "\n\t]";
                    return formattedTags;
                }
                else
                {
                    return "{}";
                }
            }
            else if (p.GenericValue is FScriptStruct gameplayTagsInArray &&
                     gameplayTagsInArray.StructType is FGameplayTagContainer gameplayTag)
            {
                var tags = gameplayTag.GameplayTags.ToList();
                if (tags.Count > 1)
                {
                    var formattedTags = "[\n" + string.Join(",\n", tags.Select(tag => $"\t\t\"{tag.TagName}\"")) +
                                        "\n\t]";
                    return formattedTags;
                }
                else if (tags.Any())
                {
                    return $"\"{tags.First().TagName}\"";
                }
                else
                {
                    return "[]";
                }
            }

            return $"\t\t\"{p.GenericValue}\"";
        })) + "\n\t]";
        shouldAppend(formattedArray);
    }

    private void ProcessChildProperties(FField[] childProperties, List<string> stringsArray,
        StringBuilder outputBuilder)
    {
        if (childProperties == null) return;

        foreach (FProperty property in childProperties)
        {
            if (!stringsArray.Contains(property.Name.PlainText))
            {
                var prefix = GetPrefix(property.GetType().Name);
                var propertyType = GetPropertyType(property);
                var isPointer = property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) ||
                                property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) ||
                                GetPropertyProperty(property);
                var pointerSuffix = isPointer ? "*" : string.Empty;
                var propertyName = property.Name.PlainText.Replace(" ", "");

                outputBuilder.AppendLine(
                    $"\t{prefix}{propertyType}{pointerSuffix} {propertyName} = {propertyName}placenolder;");
            }
        }
    }

    private void ProcessFunctions(IPackage pkg, UBlueprintGeneratedClass blueprintClass, UVerseClass verseClass,
        StringBuilder outputBuilder, bool isVerse)
    {
        var funcMapOrder = blueprintClass?.FuncMap?.Keys.Select(fname => fname.ToString()).ToList() ??
                           verseClass?.FuncMap.Keys.Select(fname => fname.ToString()).ToList();

        var functions = pkg.ExportsLazy
            .Where(e => e.Value is UFunction)
            .Select(e => (UFunction) e.Value)
            .OrderBy(f =>
            {
                if (funcMapOrder != null)
                {
                    var functionName = f.Name.ToString();
                    int index = funcMapOrder.IndexOf(functionName);
                    return index >= 0 ? index : int.MaxValue;
                }

                return int.MaxValue;
            })
            .ThenBy(f => f.Name.ToString())
            .ToList();

        foreach (var function in functions)
        {
            ProcessFunction(function, outputBuilder, isVerse);
        }
    }

    private string ProcessTextProperty(FKismetPropertyPointer property)
    {
        if (property.New is null)
        {
            return property.Old?.Name ?? string.Empty;
        }

        if (_isVerse)
        {
            return Regex.Replace(string.Join('.', property.New.Path.Select(n => n.Text)), @"^__verse_0x[0-9A-Fa-f]+_",
                "");
        }

        return string.Join('.', property.New.Path.Select(n => n.Text)).Replace(" ", "");
    }


    private void ProcessFunction(UFunction function, StringBuilder outputBuilder, bool isVerse)
    {
        string argsList = "";
        string returnFunc = "void";

        if (function?.ChildProperties != null)
        {
            foreach (FProperty property in function.ChildProperties)
            {
                if (property.Name.PlainText == "ReturnValue")
                {
                    returnFunc =
                        $"{(property.PropertyFlags.HasFlag(EPropertyFlags.ConstParm) ? "const " : string.Empty)}{GetPrefix(property.GetType().Name)}{GetPropertyType(property)}{(property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || GetPrefix(property.GetType().Name) == "U" ? "*" : string.Empty)}";
                }
                else if (!(property.Name.ToString().EndsWith("_ReturnValue") ||
                           property.Name.ToString().StartsWith("CallFunc_") ||
                           property.Name.ToString().StartsWith("K2Node_") ||
                           property.Name.ToString().StartsWith("Temp_")) ||
                         property.PropertyFlags.HasFlag(EPropertyFlags.Edit))
                {
                    argsList +=
                        $"{(property.PropertyFlags.HasFlag(EPropertyFlags.ConstParm) ? "const " : string.Empty)}{GetPrefix(property.GetType().Name)}{GetPropertyType(property)}{(property.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || GetPrefix(property.GetType().Name) == "U" ? "*" : string.Empty)}{(property.PropertyFlags.HasFlag(EPropertyFlags.OutParm) ? "&" : string.Empty)} {Regex.Replace(property.Name.ToString(), @"^__verse_0x[0-9A-Fa-f]+_", "")}, ";
                }
            }
        }

        argsList = argsList.TrimEnd(',', ' ');

        outputBuilder.AppendLine($"\n\t{returnFunc} {function.Name.Replace(" ", "")}({argsList})");
        outputBuilder.AppendLine("\t{");

        if (function?.ScriptBytecode != null)
        {
            // Generate jump code offsets map for this function
            var jumpCodeOffsets = new List<int>();
            foreach (var property in function.ScriptBytecode)
            {
                string label = null;
                int? offset = null;

                switch (property.Token)
                {
                    case EExprToken.EX_JumpIfNot:
                        label = ((EX_JumpIfNot) property).ObjectPath?.ToString()?.Split('.').Last().Split('[')[0];
                        offset = (int) ((EX_JumpIfNot) property).CodeOffset;
                        break;
                    case EExprToken.EX_Jump:
                        label = ((EX_Jump) property).ObjectPath?.ToString()?.Split('.').Last().Split('[')[0];
                        offset = (int) ((EX_Jump) property).CodeOffset;
                        break;
                    case EExprToken.EX_LocalFinalFunction:
                        EX_FinalFunction op = (EX_FinalFunction) property;
                        label = op.StackNode?.Name?.ToString()?.Split('.').Last().Split('[')[0];
                        if (op.Parameters.Length == 1 && op.Parameters[0] is EX_IntConst intConst)
                            offset = intConst.Value;
                        break;
                }

                if (!string.IsNullOrEmpty(label) && offset.HasValue && label == function.Name)
                {
                    jumpCodeOffsets.Add(offset.Value);
                }
            }

            // Process each bytecode instruction
            foreach (KismetExpression property in function.ScriptBytecode)
            {
                ProcessExpression(property.Token, property, outputBuilder, jumpCodeOffsets);
            }
        }
        else
        {
            outputBuilder.AppendLine("\t\t// This function does not have Bytecode");
        }

        outputBuilder.AppendLine("\t}");
    }

    private bool IsStringLikeProperty(FPropertyTag key)
    {
        return key.Tag.GetType().Name == "ObjectProperty" ||
               key.Tag.GetType().Name == "TextProperty" ||
               key.PropertyType == "StrProperty" ||
               key.PropertyType == "NameProperty" ||
               key.PropertyType == "ClassProperty";
    }

    public static string GetPrefix(string? type, string? extra = "")
    {
        return type switch
        {
            "FNameProperty" or "FPackageIndex" or "FTextProperty" or "FStructProperty" => "F",
            "UBlueprintGeneratedClass" or "FActorProperty" => "A",
            "FObjectProperty" when extra.Contains("Actor") => "A",
            "ResolvedScriptObject" or "ResolvedLoadedObject" or "FSoftObjectProperty" or "FObjectProperty" => "U",
            _ => ""
        };
    }

    public static string GetUnknownFieldType(object field)
    {
        string typeName = field.GetType().Name;
        int suffixIndex = typeName.IndexOf("Property", StringComparison.Ordinal);
        if (suffixIndex < 0)
            return typeName;
        return typeName.Substring(1, suffixIndex - 1);
    }

    public static string GetUnknownFieldType(FField field)
    {
        string typeName = field.GetType().Name;
        int suffixIndex = typeName.IndexOf("Property", StringComparison.Ordinal);
        if (suffixIndex < 0) return typeName;
        return typeName.Substring(1, suffixIndex - 1);
    }

    public static string GetPropertyType(object? property)
    {
        if (property is null) return "None";

        //Console.WriteLine(property.GetType().Name);
        return property switch
        {
            FIntProperty => "int",
            FInt8Property => "int8",
            FInt16Property => "int16",
            FInt64Property => "int64",
            FUInt16Property => "uint16",
            FUInt32Property => "uint32",
            FUInt64Property => "uint64",
            FBoolProperty or Boolean => "bool",
            FStrProperty => "FString",
            FFloatProperty or Single => "float",
            FDoubleProperty or Double => "double",
            FObjectProperty objct => property switch
            {
                FClassProperty clss => $"{clss.MetaClass?.Name ?? "UNKNOWN"}",
                FSoftClassProperty softClass => $"{softClass.MetaClass?.Name ?? "UNKNOWN"}",
                _ => objct.PropertyClass?.Name ?? "UNKNOWN"
            },
            FPackageIndex pkg => pkg?.ResolvedObject?.Class?.Name.ToString() ?? "Package",
            FName fme => fme.PlainText.Contains("::") ? fme.PlainText.Split("::")[0] : fme.PlainText ?? "FName",
            FEnumProperty enm => enm.Enum?.Name.ToString() ?? "Enum",
            FByteProperty bt => bt.Enum.ResolvedObject?.Name.Text ?? "Byte",
            FInterfaceProperty intrfc => $"{intrfc.InterfaceClass.Name} interface",
            FStructProperty strct => strct.Struct.ResolvedObject?.Name.Text ?? "Struct",
            FFieldPathProperty fieldPath => $"{fieldPath.PropertyClass.Text} field path",
            FDelegateProperty dlgt => $"{dlgt.SignatureFunction?.Name ?? "UNKNOWN"} (Delegate)",
            FMulticastDelegateProperty mdlgt =>
                $"{mdlgt.SignatureFunction?.Name ?? "UNKNOWN"} (MulticastDelegateProperty)",
            FMulticastInlineDelegateProperty midlgt =>
                $"{midlgt.SignatureFunction?.Name ?? "UNKNOWN"} (MulticastInlineDelegateProperty)",
            _ => GetUnknownFieldType(property)
        };
    }

    public static string GetPropertyType(FProperty? property)
    {
        if (property is null) return "None";

        return property switch
        {
            FIntProperty => "int",
            FBoolProperty => "bool",
            FStrProperty => "FString",
            FFloatProperty => "float",
            FDoubleProperty => "double",
            FObjectProperty objct => property switch
            {
                FClassProperty clss => $"{clss.MetaClass?.Name ?? "UNKNOWN"} Class",
                FSoftClassProperty softClass => $"{softClass.MetaClass?.Name ?? "UNKNOWN"} Class (soft)",
                _ => objct.PropertyClass?.Name ?? "UNKNOWN"
            },
            FEnumProperty enm => enm.Enum?.Name.ToString() ?? "Enum",
            FSetProperty set =>
                $"TSet<{GetPrefix(set.ElementProp.GetType().Name)}{GetPropertyType(set.ElementProp)}{(set.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) || set.PropertyFlags.HasFlag(EPropertyFlags.ContainsInstancedReference) ? "*" : string.Empty)}>",
            FByteProperty bt => bt.Enum.ResolvedObject?.Name.Text ?? "Byte",
            FInterfaceProperty intrfc => $"{intrfc.InterfaceClass.Name} interface",
            FStructProperty strct => strct.Struct.ResolvedObject?.Name.Text ?? "Struct",
            FFieldPathProperty fieldPath => $"{fieldPath.PropertyClass.Text} field path",
            FDelegateProperty dlgt => $"{dlgt.SignatureFunction?.Name ?? "UNKNOWN"} (Delegate)",
            FMapProperty map =>
                $"TMap<{GetPrefix(map.ValueProp.GetType().Name)}{GetPropertyType(map.KeyProp)}, {GetPrefix(map.ValueProp.GetType().Name)}{GetPropertyType(map.ValueProp)}{(map.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) || map.PropertyFlags.HasFlag(EPropertyFlags.ContainsInstancedReference) ? "*" : string.Empty)}>",
            FMulticastDelegateProperty mdlgt =>
                $"{mdlgt.SignatureFunction?.Name ?? "UNKNOWN"} (MulticastDelegateProperty)",
            FMulticastInlineDelegateProperty midlgt =>
                $"{midlgt.SignatureFunction?.Name ?? "UNKNOWN"} (MulticastInlineDelegateProperty)",
            FArrayProperty array =>
                $"TArray<{GetPrefix(array.Inner.GetType().Name)}{GetPropertyType(array.Inner)}{(array.PropertyFlags.HasFlag(EPropertyFlags.InstancedReference) || property.PropertyFlags.HasFlag(EPropertyFlags.ReferenceParm) || array.PropertyFlags.HasFlag(EPropertyFlags.ContainsInstancedReference) || GetPropertyProperty(array.Inner.GetType().Name) ? "*" : string.Empty)}>",
            _ => GetUnknownFieldType(property)
        };
    }

    public static bool GetPropertyProperty(object? property)
    {
        if (property is null) return false;

        return property switch
        {
            FObjectProperty objct => true,
            _ => false
        };
    }

    public static bool GetPropertyProperty(FProperty? property)
    {
        if (property is null) return false;

        return property switch
        {
            FObjectProperty objct => true,
            _ => false
        };
    }


    private readonly object _rawData = new();

    public void ExportData(GameFile entry, bool updateUi = true)
    {
        if (Provider.TrySavePackage(entry, out var assets))
        {
            string path = UserSettings.Default.RawDataDirectory;
            Parallel.ForEach(assets, kvp =>
            {
                lock (_rawData)
                {
                    path = Path.Combine(UserSettings.Default.RawDataDirectory,
                            UserSettings.Default.KeepDirectoryStructure ? kvp.Key : kvp.Key.SubstringAfterLast('/'))
                        .Replace('\\', '/');
                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));
                    File.WriteAllBytes(path, kvp.Value);
                }
            });

            Log.Information("{FileName} successfully exported", entry.Name);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully exported ", Constants.WHITE);
                    FLogger.Link(entry.Name, path, true);
                });
            }
        }
        else
        {
            Log.Error("{FileName} could not be exported", entry.Name);
            if (updateUi)
                FLogger.Append(ELog.Error,
                    () => FLogger.Text($"Could not export '{entry.Name}'", Constants.WHITE, true));
        }
    }

    private static bool HasFlag(EBulkType a, EBulkType b)
    {
        return (a & b) == b;
    }
}
