using System;
using System.IO;
using Xunit;
using Momo.App.ViewModels;
using Momo.Infrastructure.Subprocesses;

namespace Momo.Tests;

public class PathResolutionTests
{
    [Fact]
    public void Test_MainViewModel_Paths_Are_Resolved_Relatively()
    {
        // Save current directory to restore later
        var originalCurrentDir = Environment.CurrentDirectory;
        
        // Create a mock temp directory to simulate running from a different work directory
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // Change current directory to tempDir
            Environment.CurrentDirectory = tempDir;
            
            // Instantiating MainViewModel (without specifying database path)
            var vm = new MainViewModel();
            
            // Assert that the database path is under the application's base directory, NOT the CurrentDirectory
            var expectedBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            
            Assert.StartsWith(expectedBaseDir, vm.DbPath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(expectedBaseDir, vm.PythonScriptPath, StringComparison.OrdinalIgnoreCase);
            
            // Check SubprocessManager paths
            if (vm.SubprocessHost is SubprocessManager manager)
            {
                Assert.StartsWith(expectedBaseDir, manager.PythonExePath, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith(expectedBaseDir, manager.WorkerScriptPath, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith(expectedBaseDir, manager.DatabasePath, StringComparison.OrdinalIgnoreCase);
                
                // Assert no hardcoded d:\Antigravity drives are left in paths (unless base directory itself contains it)
                if (!expectedBaseDir.Contains(@"d:\Antigravity", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.DoesNotContain(@"d:\Antigravity", manager.PythonExePath, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(@"d:\Antigravity", manager.WorkerScriptPath, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(@"d:\Antigravity", manager.DatabasePath, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(@"d:\Antigravity", vm.DbPath, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(@"d:\Antigravity", vm.PythonScriptPath, StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                Assert.Fail("SubprocessHost is not of type SubprocessManager");
            }
        }
        finally
        {
            // Restore current directory
            Environment.CurrentDirectory = originalCurrentDir;
            
            // Clean up temp directory
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch {}
        }
    }
}
