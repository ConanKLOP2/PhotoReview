$ErrorActionPreference = 'Stop'
$source = Get-Content src/PhotoReview.Imaging/Caching/AtomicCacheFile.cs -Raw
$source = $source.Replace('using System.IO;', '').Replace('namespace PhotoReview.Imaging.Caching;', 'namespace PhotoReview.Imaging.Caching {').Replace('ILog?', 'ILog').Replace('string?', 'string')
$stubs = @'
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace PhotoReview.Imaging.Caching {
public interface ILog {}
public static class DiskCacheStore { public static void TryDelete(string path, ILog log) {if(File.Exists(path))File.Delete(path);} }
public static class OwnershipProbe {
public static string Run(string directory) {Directory.CreateDirectory(directory);var target=Path.Combine(directory,"entry.bin");var temp=target+".fixed.tmp";File.WriteAllBytes(temp,new byte[]{9,9,9});bool threw=false;try{AtomicCacheFile.WriteAsync(target,s=>s.WriteByte(1),null,temp,CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){threw=true;}return "CreateNew threw="+threw+"; original temp survives="+File.Exists(temp)+"; target exists="+File.Exists(target);}
}
}
'@
Add-Type -TypeDefinition ($stubs+$source+"`n}")
$fixture = Join-Path (Get-Location) ('TestResults/atomic-ownership-' + [guid]::NewGuid().ToString('N'))
[PhotoReview.Imaging.Caching.OwnershipProbe]::Run($fixture)
