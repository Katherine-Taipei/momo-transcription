using System.Threading.Tasks;
using Momo.Core.Entities;

namespace Momo.Core.Interfaces;

public interface ISubprocessHost
{
    Task StartWorkerAsync(Job job);
    Task StopWorkerAsync(string jobId);
    bool IsWorkerRunning(string jobId);
}
