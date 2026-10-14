using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>FEAT-EXTERNAL-EDITORS: several declared editors, the default one, per-editor arguments, the RAW companion, add / set default.</summary>
public sealed partial class MainViewModelAdvancedTests
{
    private string FakeProgram(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private async Task<(MainViewModel Vm, string Photo)> OpenPhotoAsync(string folderName, string photoName = "photo.png")
    {
        var folder = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(folder);
        var photo = CreateImageFile(folder, photoName);
        var (vm, _) = CreateViewModel();
        await vm.OpenFolderAsync(folder);
        return (vm, photo);
    }

    [Fact]
    public async Task OpenInExternalEditor_ById_LaunchesThatEditorWithItsArgumentTemplate()
    {
        var (vm, photo) = await OpenPhotoAsync("ee_by_id");
        var lightroom = new ExternalEditor { Id = "lr", Name = "Lightroom", Path = FakeProgram("lr.exe") };
        var topaz = new ExternalEditor { Id = "tp", Name = "Topaz", Path = FakeProgram("tp.exe"), Arguments = "--open {file} --fast" };
        _settings.ExternalEditors = [lightroom, topaz];
        _settings.DefaultExternalEditorId = "lr";
        var launches = new List<(string Exe, string File, string Args)>();
        vm.StartExternalEditor = (exe, file, args) => launches.Add((exe, file, args));

        vm.OpenInExternalEditor("tp");

        var launch = Assert.Single(launches);
        Assert.Equal(topaz.Path, launch.Exe);
        Assert.Equal(photo, launch.File);
        Assert.Equal($"--open \"{photo}\" --fast", launch.Args);
    }

    [Fact]
    public async Task OpenInExternalEditor_NoId_UsesTheDefaultEditor_AndAnUnknownIdDoesNothing()
    {
        var (vm, photo) = await OpenPhotoAsync("ee_default");
        _settings.ExternalEditors =
        [
            new ExternalEditor { Id = "a", Path = FakeProgram("a.exe") },
            new ExternalEditor { Id = "b", Path = FakeProgram("b.exe") },
        ];
        _settings.DefaultExternalEditorId = "b";
        var launches = new List<(string Exe, string Args)>();
        vm.StartExternalEditor = (exe, _, args) => launches.Add((exe, args));

        vm.OpenInExternalEditor("ghost");
        Assert.Empty(launches);

        vm.OpenInExternalEditor();
        var launch = Assert.Single(launches);
        Assert.EndsWith("b.exe", launch.Exe, StringComparison.Ordinal);
        Assert.Equal($"\"{photo}\"", launch.Args);
        Assert.Empty(_dialogService.SettingsTargets);
    }

    [Fact]
    public async Task OpenInExternalEditor_RawCompanionOn_SendsTheRawOriginal_Off_SendsTheShownFile()
    {
        var (vm, photo) = await OpenPhotoAsync("ee_raw", "shot.png");
        var raw = Path.Combine(Path.GetDirectoryName(photo)!, "shot.cr3"); // same name, same folder, known RAW extension
        File.WriteAllBytes(raw, [1]);
        _settings.ExternalEditors = [new ExternalEditor { Id = "a", Path = FakeProgram("raw-editor.exe") }];
        var sent = new List<string>();
        vm.StartExternalEditor = (_, file, _) => sent.Add(file);

        _settings.ExternalEditorSendsRawOriginal = true;
        vm.OpenInExternalEditor();
        _settings.ExternalEditorSendsRawOriginal = false;
        vm.OpenInExternalEditor();

        Assert.Equal([raw, photo], sent);
    }

    [Fact]
    public async Task OpenInExternalEditor_EditorProgramMissing_ShowsAnErrorAndDoesNotLaunch()
    {
        var (vm, _) = await OpenPhotoAsync("ee_exe_missing");
        _settings.ExternalEditors = [new ExternalEditor { Id = "a", Path = Path.Combine(_tempDir, "no-such-editor.exe") }];
        var launched = false;
        vm.StartExternalEditor = (_, _, _) => launched = true;

        vm.OpenInExternalEditor();

        Assert.False(launched);
        Assert.Single(_dialogService.Errors);
    }

    [Fact]
    public async Task AddExternalEditor_AppendsTheChosenEditor_AssignsAFreshId_AndKeepsTheDefault()
    {
        var (vm, _) = await OpenPhotoAsync("ee_add");
        _settings.ExternalEditors = [new ExternalEditor { Id = "a", Name = "First", Path = FakeProgram("first.exe") }];
        _settings.DefaultExternalEditorId = "a";
        _dialogService.AddEditorResponse = new ExternalEditor { Id = "a", Name = "Second", Path = FakeProgram("second.exe") }; // same id on purpose

        vm.AddExternalEditor();

        Assert.Equal(["First", "Second"], _settings.ExternalEditors.Select(editor => editor.Name));
        Assert.Equal(2, _settings.ExternalEditors.Select(editor => editor.Id).Distinct().Count());
        Assert.Equal("a", _settings.DefaultExternalEditorId);
        Assert.Equal(1, _dialogService.AddEditorPrompts);
    }

    [Fact]
    public async Task AddExternalEditor_Cancelled_ChangesNothing()
    {
        var (vm, _) = await OpenPhotoAsync("ee_add_cancel");
        _dialogService.AddEditorResponse = null;

        vm.AddExternalEditor();

        Assert.Empty(_settings.ExternalEditors);
    }

    [Fact]
    public async Task AddExternalEditor_WhenOnlyTheLegacyPathExists_KeepsItAsTheDefaultAndAppends()
    {
        var (vm, _) = await OpenPhotoAsync("ee_add_legacy");
        _settings.ExternalEditorPath = FakeProgram("legacy.exe");
        _dialogService.AddEditorResponse = new ExternalEditor { Name = "New", Path = FakeProgram("new.exe") };

        vm.AddExternalEditor();

        Assert.Equal(2, _settings.ExternalEditors.Count);
        Assert.Equal("legacy", _settings.ExternalEditors[0].Name);
        Assert.Equal(_settings.ExternalEditors[0].Id, _settings.DefaultExternalEditorId);
        Assert.Equal(_settings.ExternalEditors[0].Path, _settings.ExternalEditorPath);
    }

    [Fact]
    public async Task ChooseDefaultExternalEditor_StoresTheChosenId_AndMirrorsItsPath()
    {
        var (vm, _) = await OpenPhotoAsync("ee_choose");
        _settings.ExternalEditors =
        [
            new ExternalEditor { Id = "a", Path = FakeProgram("ca.exe") },
            new ExternalEditor { Id = "b", Path = FakeProgram("cb.exe") },
        ];
        _settings.DefaultExternalEditorId = "a";
        _dialogService.DefaultEditorResponse = "b";

        vm.ChooseDefaultExternalEditor();

        Assert.Equal(["a", "b"], _dialogService.DefaultEditorChoices);
        Assert.Equal("b", _settings.DefaultExternalEditorId);
        Assert.EndsWith("cb.exe", _settings.ExternalEditorPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChooseDefaultExternalEditor_CancelledOrUnknownId_KeepsTheDefault()
    {
        var (vm, _) = await OpenPhotoAsync("ee_choose_cancel");
        _settings.ExternalEditors = [new ExternalEditor { Id = "a", Path = FakeProgram("xa.exe") }, new ExternalEditor { Id = "b", Path = FakeProgram("xb.exe") }];
        _settings.DefaultExternalEditorId = "a";

        _dialogService.DefaultEditorResponse = null;
        vm.ChooseDefaultExternalEditor();
        _dialogService.DefaultEditorResponse = "nope";
        vm.ChooseDefaultExternalEditor();

        Assert.Equal("a", _settings.DefaultExternalEditorId);
    }
}
