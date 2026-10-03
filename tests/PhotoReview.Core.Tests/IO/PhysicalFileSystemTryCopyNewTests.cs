using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Tests.IO;

/// <summary>
/// <see cref="PhysicalFileSystem.TryCopyNew"/> against the real disk (temp directory, self-cleaning): only the
/// "destination already exists" failure becomes <c>false</c>; every other failure must surface as an exception, because the
/// caller treats <c>false</c> as "nothing of mine is at the destination".
/// </summary>
public sealed class PhysicalFileSystemTryCopyNewTests
{
    [Fact]
    public void TryCopyNew_DestinationIsADirectory_ThrowsInsteadOfReportingFalse()
    {
        using var root = new TempRoot("trycopynew-dir");
        var source = root.File("a.txt", 1, 2, 3);
        var destination = root.Dir("b.txt"); // a directory where the file would go

        Assert.ThrowsAny<Exception>(() => new PhysicalFileSystem().TryCopyNew(source, destination));

        Assert.True(Directory.Exists(destination)); // untouched
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
    }

    [Fact]
    [Trait("Category", "Native")] // changes a real ACL: a killed test host would leave a Deny ACE behind (the finally restores it otherwise)
    [SupportedOSPlatform("windows")]
    public void TryCopyNew_DestinationFolderDeniesCreatingFiles_ThrowsAccessDeniedAndCreatesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TempRoot("trycopynew-acl");
        var source = root.File("a.txt", 1, 2, 3);
        var folder = new DirectoryInfo(root.Dir("locked"));
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.CreateFiles | FileSystemRights.WriteData, AccessControlType.Deny);
        var security = folder.GetAccessControl();
        security.AddAccessRule(deny);
        folder.SetAccessControl(security);
        try
        {
            var destination = Path.Combine(folder.FullName, "a.txt");

            Assert.Throws<UnauthorizedAccessException>(() => new PhysicalFileSystem().TryCopyNew(source, destination));

            Assert.False(File.Exists(destination));
        }
        finally
        {
            var restore = folder.GetAccessControl();
            restore.RemoveAccessRule(deny); // self-cleaning: the temp directory must be deletable again
            folder.SetAccessControl(restore);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070050), true)]   // ERROR_FILE_EXISTS (80)
    [InlineData(unchecked((int)0x800700B7), true)]   // ERROR_ALREADY_EXISTS (183)
    [InlineData(unchecked((int)0x80070005), false)]  // ERROR_ACCESS_DENIED
    [InlineData(unchecked((int)0x80070020), false)]  // ERROR_SHARING_VIOLATION
    [InlineData(unchecked((int)0x80070070), false)]  // ERROR_DISK_FULL: low word 0x70, not 0x50
    [InlineData(0x00000050, false)]                  // low word 80 but not a Win32 HRESULT
    [InlineData(unchecked((int)0x800B0050), false)]  // low word 80 in another facility
    [InlineData(unchecked((int)0x000700B7), true)]   // facility bits without the severity bit still count: same Win32 facility
    public void IsDestinationExists_OnlyWin32FileExistsAndAlreadyExists_AreRecognized(int hresult, bool expected)
    {
        Assert.Equal(expected, FileSystemErrors.IsDestinationExists(new IOException("x", hresult)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070050))] // ERROR_FILE_EXISTS with the failure bit
    [InlineData(unchecked((int)0x800700B7))] // ERROR_ALREADY_EXISTS with the failure bit
    [InlineData(0x00070050)]                 // the same codes without the severity bit (facility 7 only)
    [InlineData(0x000700B7)]
    public void IsSwallowedAsDestinationExists_DestinationExistsHResults_AreSwallowed(int hresult)
    {
        Assert.True(PhysicalFileSystem.IsSwallowedAsDestinationExists(new IOException("x", hresult)));
    }

    public static TheoryData<Exception> RethrownFailures => new()
    {
        new UnauthorizedAccessException("denied"),
        new DirectoryNotFoundException("missing folder"),
        new IOException("no hresult"),
        new IOException("sharing violation", unchecked((int)0x80070020)),
        new IOException("disk full", unchecked((int)0x80070070)),
        new IOException("file exists code but not Win32", 0x00000050),
        new InvalidOperationException("other") { HResult = unchecked((int)0x800700B7) },
    };

    [Theory]
    [MemberData(nameof(RethrownFailures))]
    public void IsSwallowedAsDestinationExists_EveryOtherFailure_IsRethrown(Exception failure)
    {
        Assert.False(PhysicalFileSystem.IsSwallowedAsDestinationExists(failure));
    }

    [Fact]
    public void TryCopyNew_ExistingFileAtDestination_ReturnsFalseAndKeepsItUntouched()
    {
        using var root = new TempRoot("trycopynew-exists");
        var source = root.File("a.txt", 1, 2, 3);
        var destination = root.File("b.txt", 9);

        Assert.False(new PhysicalFileSystem().TryCopyNew(source, destination));

        Assert.Equal([9], File.ReadAllBytes(destination));
    }

    [Fact]
    public void TryCopyNew_FailsForADirectoryDestination_LeavesNoFileBehind()
    {
        using var root = new TempRoot("trycopynew-nofile");
        var source = root.File("a.txt", 1, 2, 3);
        var destination = root.Dir("b.txt");

        Assert.ThrowsAny<Exception>(() => new PhysicalFileSystem().TryCopyNew(source, destination));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
        Assert.Equal(["a.txt", "b.txt"], Directory.GetFileSystemEntries(root.Path).Select(entry => System.IO.Path.GetFileName(entry)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void IsDestinationExists_NonIoExceptionWithTheSameHResult_IsNotRecognized()
    {
        Assert.False(FileSystemErrors.IsDestinationExists(new InvalidOperationException("x") { HResult = unchecked((int)0x800700B7) }));
    }
}
