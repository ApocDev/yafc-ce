﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SDL2;
using Serilog;
using Yafc.Model;
using Yafc.Parser;
using Yafc.UI;

namespace Yafc;

public class WelcomeScreen : WindowUtility, IProgress<(string, string)>, IKeyboardFocus {
    private readonly ILogger logger = Logging.GetLogger<WelcomeScreen>();
    private bool loading;
    private string? currentLoad1, currentLoad2;
    private string path = "", dataPath = "", _modsPath = "";
    private string modsPath {
        get => _modsPath;
        set {
            _modsPath = value;
            FactorioDataSource.ClearDisabledMods();
        }
    }
    private bool netProduction;
    private string createText;
    private bool canCreate;
    private readonly ScrollArea errorScroll;
    private readonly ScrollArea recentProjectScroll;
    private readonly ScrollArea languageScroll;
    private string? errorMod;
    private string? errorMessage;
    private string? tip;
    private readonly string[] tips;

    private static readonly Dictionary<string, string> languageMapping = new Dictionary<string, string>()
    {
        {"en", "English"},
        {"ca", "Catalan"},
        {"cs", "Czech"},
        {"da", "Danish"},
        {"nl", "Dutch"},
        {"de", "German"},
        {"fi", "Finnish"},
        {"fr", "French"},
        {"hu", "Hungarian"},
        {"it", "Italian"},
        {"no", "Norwegian"},
        {"pl", "Polish"},
        {"pt-PT", "Portuguese"},
        {"pt-BR", "Portuguese (Brazilian)"},
        {"ro", "Romanian"},
        {"ru", "Russian"},
        {"es-ES", "Spanish"},
        {"sv-SE", "Swedish"},
        {"tr", "Turkish"},
        {"uk", "Ukrainian"},
    };

    private static readonly Dictionary<string, string> languagesRequireFontOverride = new Dictionary<string, string>()
    {
        {"ja", "Japanese"},
        {"zh-CN", "Chinese (Simplified)"},
        {"zh-TW", "Chinese (Traditional)"},
        {"ko", "Korean"},
        {"tr", "Turkish"},
    };

    private enum EditType {
        Workspace, Factorio, Mods
    }

    public WelcomeScreen(ProjectDefinition? cliProject = null) : base(ImGuiUtils.DefaultScreenPadding) {
        tips = File.ReadAllLines("Data/Tips.txt");

        IconCollection.ClearCustomIcons();
        RenderingUtils.SetColorScheme(Preferences.Instance.darkMode);

        recentProjectScroll = new ScrollArea(20f, BuildRecentProjectList, collapsible: true);
        languageScroll = new ScrollArea(20f, LanguageSelection, collapsible: true);
        errorScroll = new ScrollArea(20f, BuildError, collapsible: true);
        Create("Welcome to YAFC CE v" + YafcLib.version.ToString(3), 45, null);

        if (cliProject != null && !string.IsNullOrEmpty(cliProject.dataPath)) {
            SetProject(cliProject);
            LoadProject();
        }
        else {
            ProjectDefinition? lastProject = Preferences.Instance.recentProjects.FirstOrDefault();
            SetProject(lastProject);
            _ = InputSystem.Instance.SetDefaultKeyboardFocus(this);
        }
    }

    private void BuildError(ImGui gui) {
        if (errorMod != null) {
            gui.BuildText($"Error while loading mod {errorMod}.", TextBlockDisplayStyle.Centered with { Color = SchemeColor.Error });
        }

        gui.allocator = RectAllocator.Stretch;
        gui.BuildText(errorMessage, TextBlockDisplayStyle.ErrorText with { Color = SchemeColor.ErrorText });
        gui.DrawRectangle(gui.lastRect, SchemeColor.Error);
    }

    protected override void BuildContents(ImGui gui) {
        gui.spacing = 1.5f;
        gui.BuildText("Yet Another Factorio Calculator", new TextBlockDisplayStyle(Font.header, Alignment: RectAlignment.Middle));
        if (loading) {
            gui.BuildText(currentLoad1, TextBlockDisplayStyle.Centered);
            gui.BuildText(currentLoad2, TextBlockDisplayStyle.Centered);
            gui.AllocateSpacing(15f);
            gui.BuildText(tip, new TextBlockDisplayStyle(WrapText: true, Alignment: RectAlignment.Middle));
            gui.SetNextRebuild(Ui.time + 30);
        }
        else if (errorMessage != null) {
            errorScroll.Build(gui);
            bool thereIsAModToDisable = (errorMod != null);

            using (gui.EnterRow()) {
                if (thereIsAModToDisable) {
                    gui.BuildWrappedText("YAFC was unable to load the project. You can disable the problematic mod once by clicking on 'Disable & reload' button, or you can disable it " +
                                         "permanently for YAFC by copying the mod-folder, disabling the mod in the copy by editing mod-list.json, and pointing YAFC to the copy.");
                }
                else {
                    gui.BuildWrappedText("YAFC cannot proceed because it was unable to load the project.");
                }
            }

            using (gui.EnterRow()) {
                if (gui.BuildLink("More info")) {
                    ShowDropDown(gui, gui.lastRect, ProjectErrorMoreInfo, new Padding(0.5f), 30f);
                }
            }

            using (gui.EnterRow()) {
                if (gui.BuildButton("Copy to clipboard", SchemeColor.Grey)) {
                    _ = SDL.SDL_SetClipboardText(errorMessage);
                }
                if (thereIsAModToDisable && gui.BuildButton("Disable & reload").WithTooltip(gui, "Disable this mod until you close YAFC or change the mod folder.")) {
                    FactorioDataSource.DisableMod(errorMod!);
                    errorMessage = null;
                    LoadProject();
                }
                if (gui.RemainingRow().BuildButton("Back")) {
                    errorMessage = null;
                    Rebuild();
                }
            }
        }
        else {
            BuildPathSelect(gui, path, "Project file location", "You can leave it empty for a new project", EditType.Workspace);
            BuildPathSelect(gui, dataPath, "Factorio Data location*\nIt should contain folders 'base' and 'core'",
                "e.g. C:/Games/Steam/SteamApps/common/Factorio/data", EditType.Factorio);
            BuildPathSelect(gui, modsPath, "Factorio Mods location (optional)\nIt should contain file 'mod-list.json'",
                "If you don't use separate mod folder, leave it empty", EditType.Mods);

            using (gui.EnterRow()) {
                gui.allocator = RectAllocator.RightRow;
                string lang = Preferences.Instance.language;
                if (languageMapping.TryGetValue(Preferences.Instance.language, out string? mapped) || languagesRequireFontOverride.TryGetValue(Preferences.Instance.language, out mapped)) {
                    lang = mapped;
                }

                if (gui.BuildLink(lang)) {
                    gui.ShowDropDown(languageScroll.Build);
                }

                gui.BuildText("In-game objects language:");
            }

            using (gui.EnterRowWithHelpIcon("""
                    If checked, YAFC will only suggest production or consumption recipes that have a net production or consumption of that item or fluid.
                    For example, kovarex enrichment will not be suggested when adding recipes that produce U-238 or consume U-235.
                    """, false)) {
                _ = gui.BuildCheckBox("Use net production/consumption when analyzing recipes", netProduction, out netProduction);
            }

            string softwareRenderHint = "If checked, the main project screen will not use hardware-accelerated rendering.\n\n" +
                "Enable this setting if YAFC crashes after loading without an error message, or if you know that your computer's " +
                "graphics hardware does not support modern APIs (e.g. DirectX 12 on Windows).";

            using (gui.EnterRowWithHelpIcon(softwareRenderHint, false)) {
                bool forceSoftwareRenderer = Preferences.Instance.forceSoftwareRenderer;
                _ = gui.BuildCheckBox("Force software rendering in project screen", forceSoftwareRenderer, out forceSoftwareRenderer);

                if (forceSoftwareRenderer != Preferences.Instance.forceSoftwareRenderer) {
                    Preferences.Instance.forceSoftwareRenderer = forceSoftwareRenderer;
                    Preferences.Instance.Save();
                }
            }

            using (gui.EnterRow()) {
                if (Preferences.Instance.recentProjects.Length > 1) {
                    if (gui.BuildButton("Recent projects", SchemeColor.Grey)) {
                        gui.ShowDropDown(BuildRecentProjectsDropdown, 35f);
                    }
                }
                if (gui.BuildButton(Icon.Help).WithTooltip(gui, "About YAFC")) {
                    _ = new AboutScreen(this);
                }

                if (gui.BuildButton(Icon.DarkMode).WithTooltip(gui, "Toggle dark mode")) {
                    Preferences.Instance.darkMode = !Preferences.Instance.darkMode;
                    RenderingUtils.SetColorScheme(Preferences.Instance.darkMode);
                    Preferences.Instance.Save();
                }
                if (gui.RemainingRow().BuildButton(createText, active: canCreate)) {
                    LoadProject();
                }
            }
        }
    }

    private void ProjectErrorMoreInfo(ImGui gui) {
        void buildWrappedText(string message) => gui.BuildText(message, TextBlockDisplayStyle.WrappedText);

        buildWrappedText("Check that these mods load in Factorio.");
        buildWrappedText("YAFC only supports loading mods that were loaded in Factorio before. If you add or remove mods or change startup settings, " +
            "you need to load those in Factorio and then close the game because Factorio writes some files only when exiting.");
        buildWrappedText("Check that Factorio loads mods from the same folder as YAFC.");
        buildWrappedText("If that doesn't help, try removing the mods that have several versions, or are disabled, or don't have the required dependencies.");

        // The whole line is underlined if the allocator is not set to LeftAlign
        gui.allocator = RectAllocator.LeftAlign;
        if (gui.BuildLink("If all else fails, then create an issue on GitHub")) {
            Ui.VisitLink(AboutScreen.Github);
        }

        buildWrappedText("Please attach a new-game save file to sync mods, versions, and settings.");
    }

    private static void DoLanguageList(ImGui gui, Dictionary<string, string> list, bool enabled) {
        foreach (var (k, v) in list) {
            if (!enabled) {
                gui.BuildText(v);
            }
            else if (gui.BuildLink(v)) {
                Preferences.Instance.language = k;
                Preferences.Instance.Save();
                _ = gui.CloseDropdown();
            }
        }
    }

    private void LanguageSelection(ImGui gui) {
        gui.spacing = 0f;
        gui.allocator = RectAllocator.LeftAlign;
        gui.BuildText("Mods may not support your language, using English as a fallback.", TextBlockDisplayStyle.WrappedText);
        gui.AllocateSpacing(0.5f);

        DoLanguageList(gui, languageMapping, true);
        if (!Program.hasOverriddenFont) {
            gui.AllocateSpacing(0.5f);

            string nonEuLanguageMessage = "To select languages with non-European glyphs you need to override used font first. Download or locate a font that has your language glyphs.";
            gui.BuildText(nonEuLanguageMessage, TextBlockDisplayStyle.WrappedText);
            gui.AllocateSpacing(0.5f);
        }
        DoLanguageList(gui, languagesRequireFontOverride, Program.hasOverriddenFont);

        gui.AllocateSpacing(0.5f);
        if (gui.BuildButton("Select font to override")) {
            SelectFont();
        }

        if (Preferences.Instance.overrideFont != null) {
            gui.BuildText(Preferences.Instance.overrideFont, TextBlockDisplayStyle.WrappedText);
            if (gui.BuildLink("Reset font to default")) {
                Preferences.Instance.overrideFont = null;
                languageScroll.RebuildContents();
                Preferences.Instance.Save();
            }
        }
        gui.BuildText("Selecting font to override require YAFC restart to take effect", TextBlockDisplayStyle.WrappedText);
    }

    private async void SelectFont() {
        string? result = await new FilesystemScreen("Override font", "Override font that YAFC uses", "Ok", null, FilesystemScreen.Mode.SelectFile, null, this, null, null);
        if (result == null) {
            return;
        }

        if (SDL_ttf.TTF_OpenFont(result, 16) != IntPtr.Zero) {
            Preferences.Instance.overrideFont = result;
            languageScroll.RebuildContents();
            Preferences.Instance.Save();
        }
    }

    public void Report((string, string) value) => (currentLoad1, currentLoad2) = value;

    private bool FactorioValid(string factorio) => !string.IsNullOrEmpty(factorio) && Directory.Exists(Path.Combine(factorio, "core"));

    private bool ModsValid(string mods) => string.IsNullOrEmpty(mods) || File.Exists(Path.Combine(mods, "mod-list.json"));

    [MemberNotNull(nameof(createText))]
    private void ValidateSelection() {
        bool factorioValid = FactorioValid(dataPath);
        bool modsValid = ModsValid(modsPath);
        bool projectExists = File.Exists(path);

        if (projectExists) {
            createText = "Load '" + Path.GetFileNameWithoutExtension(path) + "'";
        }
        else if (path != "") {
            string? directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) {
                createText = "Project directory does not exist";
                canCreate = false;
                return;
            }
            createText = "Create '" + Path.GetFileNameWithoutExtension(path) + "'";
        }
        else {
            createText = "Create new project";
        }

        canCreate = factorioValid && modsValid;
    }

    private void BuildPathSelect(ImGui gui, string path, string description, string placeholder, EditType editType) {
        gui.BuildText(description, TextBlockDisplayStyle.WrappedText);
        gui.spacing = 0.5f;
        using (gui.EnterGroup(default, RectAllocator.RightRow)) {
            if (gui.BuildButton("...")) {
                ShowFileSelect(description, path, editType);
            }

            if (gui.RemainingRow(0f).BuildTextInput(path, out path, placeholder)) {
                switch (editType) {
                    case EditType.Workspace:
                        this.path = path;
                        break;
                    case EditType.Factorio:
                        dataPath = path;
                        break;
                    case EditType.Mods:
                        modsPath = path;
                        break;
                }
                ValidateSelection();
            }
        }
        gui.spacing = 1.5f;
    }

    /// <summary>
    /// Initializes different input fields with the supplied project definition. <br/>
    /// If the project is null, the fields are cleared. <br/>
    /// If the user is on Windows, it also tries to infer the installation directory of Factorio.
    /// </summary>
    /// <param name="project">A project definition with paths and options. Can be null.</param>
    [MemberNotNull(nameof(createText))]
    private void SetProject(ProjectDefinition? project) {
        if (project != null) {
            dataPath = project.dataPath;
            modsPath = project.modsPath;
            path = project.path;
            netProduction = project.netProduction;
        }
        else {
            dataPath = "";
            modsPath = "";
            path = "";
            netProduction = false;
        }

        if (dataPath == "" && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            string possibleDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam/steamApps/common/Factorio/data");
            if (FactorioValid(possibleDataPath)) {
                dataPath = possibleDataPath;
            }
        }

        ValidateSelection();
        rootGui.Rebuild();
    }

    private async void LoadProject() {
        try {
            // TODO (shpaass/yafc-ce/issues/249): Why does WelcomeScreen.cs need the internals of ProjectDefinition?
            // Why not take or copy the whole object? The parts are used only in WelcomeScreen.cs, so I see no reason
            // to disassemble ProjectDefinition and drag it piece by piece.
            var (dataPath, modsPath, projectPath) = (this.dataPath, this.modsPath, path);
            Preferences.Instance.AddProject(dataPath, modsPath, projectPath, netProduction);
            Preferences.Instance.Save();
            tip = tips.Length > 0 ? tips[DataUtils.random.Next(tips.Length)] : "";

            loading = true;
            rootGui.Rebuild();

            await Ui.ExitMainThread();

            ErrorCollector collector = new ErrorCollector();
            var project = FactorioDataSource.Parse(dataPath, modsPath, projectPath, netProduction, this, collector, Preferences.Instance.language);

            await Ui.EnterMainThread();
            logger.Information("Opening main screen");
            _ = new MainScreen(displayIndex, project);

            if (collector.severity > ErrorSeverity.None) {
                ErrorListPanel.Show(collector);
            }

            Close();
            GC.Collect();
            logger.Information("GC: {TotalMemory}", GC.GetTotalMemory(false));
        }
        catch (Exception ex) {
            await Ui.EnterMainThread();
            while (ex.InnerException != null) {
                ex = ex.InnerException;
            }

            errorScroll.RebuildContents();
            errorMod = FactorioDataSource.CurrentLoadingMod;
            if (ex is LuaException lua) {
                errorMessage = lua.Message;
            }
            else {
                errorMessage = ex.Message + "\n" + ex.StackTrace;
            }
        }
        finally {
            loading = false;
            rootGui.Rebuild();
        }
    }

    private Func<string, bool>? GetFolderFilter(EditType type) => type switch {
        EditType.Mods => ModsValid,
        EditType.Factorio => FactorioValid,
        _ => null,
    };

    private async void ShowFileSelect(string description, string path, EditType type) {
        string buttonText;
        string? location, fileExtension;
        FilesystemScreen.Mode fsMode;

        if (type == EditType.Workspace) {
            buttonText = "Select";
            location = Path.GetDirectoryName(path);
            fsMode = FilesystemScreen.Mode.SelectOrCreateFile;
            fileExtension = "yafc";
        }
        else {
            buttonText = "Select folder";
            location = path;
            fsMode = FilesystemScreen.Mode.SelectFolder;
            fileExtension = null;
        }

        string? result = await new FilesystemScreen("Select folder", description, buttonText, location, fsMode, "", this, GetFolderFilter(type), fileExtension);

        if (result != null) {
            if (type == EditType.Factorio) {
                dataPath = result;
            }
            else if (type == EditType.Mods) {
                modsPath = result;
            }
            else {
                this.path = result;
            }

            Rebuild();
            ValidateSelection();
        }
    }

    private void BuildRecentProjectsDropdown(ImGui gui) => recentProjectScroll.Build(gui);

    private void BuildRecentProjectList(ImGui gui) {
        gui.spacing = 0f;
        foreach (var project in Preferences.Instance.recentProjects) {
            if (string.IsNullOrEmpty(project.path)) {
                continue;
            }

            using (gui.EnterGroup(new Padding(0.5f, 0.25f), RectAllocator.LeftRow)) {
                gui.BuildIcon(Icon.Settings);
                gui.RemainingRow(0.5f).BuildText(project.path);
            }

            if (gui.BuildButton(gui.lastRect, SchemeColor.None, SchemeColor.Grey)) {
                WelcomeScreen owner = (WelcomeScreen)gui.window!; // null-forgiving: gui.window has been set earlier in the render loop.
                owner.SetProject(project);
                _ = gui.CloseDropdown();
            }
        }
    }

    public bool KeyDown(SDL.SDL_Keysym key) {
        if (canCreate && !loading && errorMessage == null
            && key.scancode is SDL.SDL_Scancode.SDL_SCANCODE_RETURN or SDL.SDL_Scancode.SDL_SCANCODE_RETURN2 or SDL.SDL_Scancode.SDL_SCANCODE_KP_ENTER) {

            LoadProject();
            return true;
        }
        if (errorMessage != null && key.scancode == SDL.SDL_Scancode.SDL_SCANCODE_ESCAPE) {
            errorMessage = null;
            Rebuild();
            return true;
        }
        return false;
    }
    public bool TextInput(string input) => false;
    public bool KeyUp(SDL.SDL_Keysym key) => false;
    public void FocusChanged(bool focused) { }
}
