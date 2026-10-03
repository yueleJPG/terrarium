using System.Diagnostics;
using Terrarium.Core;

namespace Terrarium.App;

/// <summary>
/// 在后台线程驱动模拟。
///
/// 线程模型很简单也很安全：模拟线程独占 World，UI 线程只通过 Simulation.Snapshot()
/// 拿只读快照。两者之间唯一的共享点是 Simulation 内部那把锁，
/// 而 RunBatch 是"每 tick 加一次锁"，所以 UI 不会因为快进而卡死。
/// </summary>
internal sealed class SimulationRunner : IDisposable
{
    /// <summary>真实时间基准：20 tick = 1 秒。速度倍率就是在此基础上乘。</summary>
    public const int BaseTicksPerSecond = 20;

    private readonly Thread _thread;
    private volatile bool _stop;

    public Simulation Sim { get; private set; }
    public volatile bool Paused;
    /// <summary>速度倍率；<see cref="MaxSpeed"/> 表示不限速跑满 CPU。</summary>
    public volatile int Speed = 1;
    public const int MaxSpeed = -1;

    /// <summary>实测的 tick/s，显示在状态栏上。</summary>
    public volatile int MeasuredTicksPerSecond;

    public SimulationRunner(Simulation sim)
    {
        Sim = sim;
        _thread = new Thread(Loop) { IsBackground = true, Name = "TerrariumSim" };
        _thread.Start();
    }

    public void ReplaceSimulation(Simulation sim)
    {
        Sim = sim;
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long lastMs = 0;
        long ticksSinceMeasure = 0;
        long measureStartMs = 0;

        // 小数累加器。少了它，低倍速下每轮算出来的预算会被 (int) 截断成 0，
        // 循环空转到天荒地老、模拟一 tick 都不推进 —— 而且界面看起来一切正常。
        double accumulator = 0;

        while (!_stop)
        {
            long nowMs = sw.ElapsedMilliseconds;
            long dt = nowMs - lastMs;
            lastMs = nowMs;
            if (dt < 0) dt = 0;
            if (dt > 250) dt = 250;   // 窗口被拖动 / 系统卡顿后不要一次性补一大堆

            if (Paused || Speed == 0)
            {
                accumulator = 0;
                Thread.Sleep(12);
                continue;
            }

            var sim = Sim;
            int budget;
            if (Speed == MaxSpeed)
            {
                budget = 500;         // 极速：每轮固定跑一批，把 CPU 跑满
            }
            else
            {
                accumulator += dt * 0.001 * BaseTicksPerSecond * Speed;
                budget = (int)accumulator;
                if (budget > 20000) budget = 20000;
                accumulator -= budget;
                if (accumulator > 20000) accumulator = 0;
            }

            if (budget <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            int done = sim.RunBatch(budget, () => Paused || _stop);
            ticksSinceMeasure += done;

            if (done == 0)
            {
                if (sim.World.Extinct) Paused = true;
                Thread.Sleep(1);
            }
            else if (done < 4 && Speed != MaxSpeed)
            {
                // 让出 CPU，别在低倍速下空转烧一个核
                Thread.Sleep(1);
            }

            if (nowMs - measureStartMs >= 1000)
            {
                MeasuredTicksPerSecond = (int)(ticksSinceMeasure * 1000 / Math.Max(1, nowMs - measureStartMs));
                ticksSinceMeasure = 0;
                measureStartMs = nowMs;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(400);
    }
}
