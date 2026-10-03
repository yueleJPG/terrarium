using System.Diagnostics;
using Terrarium.Core;

namespace Terrarium.Android;

/// <summary>
/// 在后台线程驱动模拟。
///
/// 这套线程模型是从桌面版原样搬过来的，因为它已经被验证过：
/// 模拟线程独占 World，UI 线程只通过 Simulation.Snapshot() 拿只读快照，
/// 两者之间唯一的共享点是 Simulation 内部那把锁。
///
/// 手机上多了一层考虑：**速度上限要低得多**。桌面版有"不限速跑满 CPU"这一档，
/// 手机上那么干会立刻发烫降频，所以最高只给 60×。
/// </summary>
internal sealed class SimRunner : IDisposable
{
    /// <summary>真实时间基准：20 tick = 1 秒。</summary>
    public const int BaseTicksPerSecond = 20;

    /// <summary>可选的倍速档位。刻意不含"不限速" —— 手机会发烫。</summary>
    public static readonly int[] SpeedSteps = { 1, 2, 4, 8, 20, 60 };

    private readonly Thread _thread;
    private volatile bool _stop;

    public Simulation Sim { get; private set; }
    public volatile bool Paused;
    public volatile int Speed = 4;

    /// <summary>实测 tick/s，显示在状态栏上（手机上这个数字直接反映耗电速度）。</summary>
    public volatile int MeasuredTicksPerSecond;

    /// <summary>
    /// 模拟线程挂掉时记下的异常文本。
    ///
    /// 后台线程上没被捕获的异常在 Android 上会**直接杀掉整个进程**，
    /// 表现就是"玩着玩着应用自己没了" —— 用户除了重开别无办法，也无从反馈。
    /// 这里把它兜住、停下循环、让界面把原因显示出来。
    /// </summary>
    public volatile string? FatalError;

    public SimRunner(Simulation sim)
    {
        Sim = sim;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "TerrariumSim",
            // 后台线程降一级优先级：手机上模拟绝不能和 UI 抢 CPU，
            // 否则拖动地图会掉帧，而掉帧比模拟慢一点难看得多。
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long lastMs = 0, ticksSinceMeasure = 0, measureStartMs = 0;

        // 小数累加器。少了它，低倍速下每轮算出来的预算会被 (int) 截断成 0，
        // 循环空转到天荒地老、模拟一 tick 都不推进 —— 而且界面看起来一切正常。
        // 这个坑在桌面版上踩过一次，这里直接带上。
        double accumulator = 0;

        while (!_stop)
        {
            long nowMs = sw.ElapsedMilliseconds;
            long dt = nowMs - lastMs;
            lastMs = nowMs;
            if (dt < 0) dt = 0;
            if (dt > 250) dt = 250;   // 系统卡顿后不要一次性补一大堆

            if (!Paused)
            {
                accumulator += dt * 0.001 * BaseTicksPerSecond * Speed;
                int budget = (int)accumulator;
                if (budget > 0)
                {
                    accumulator -= budget;
                    if (budget > 2000) budget = 2000;   // 单轮上限，别让一帧变成长阻塞
                    try
                    {
                        Sim.RunBatch(budget);
                        ticksSinceMeasure += budget;
                    }
                    catch (Exception ex)
                    {
                        // 继续跑没有意义：World 可能已经处于半更新状态，
                        // 再跑下去要么重复抛、要么产出垃圾数据。停下来，让用户看见原因。
                        FatalError = ex.ToString();
                        break;
                    }
                }
            }
            else
            {
                accumulator = 0;
            }

            if (nowMs - measureStartMs >= 1000)
            {
                MeasuredTicksPerSecond = (int)(ticksSinceMeasure * 1000 / Math.Max(1, nowMs - measureStartMs));
                ticksSinceMeasure = 0;
                measureStartMs = nowMs;
            }

            // 手机上别空转：给 UI 线程留出时间片。
            // 桌面版这里是 SpinWait，搬到手机上会白烧电池。
            Thread.Sleep(Paused ? 60 : 4);
        }
    }

    /// <summary>
    /// 换一个生态箱（"新建"用）。
    ///
    /// 引用赋值本身是原子的，循环下一轮自然就跑到新的 Simulation 上 ——
    /// 不需要停线程。最坏情况是旧的 World 多跑了一 tick，用户看不出来。
    /// </summary>
    public void ReplaceSimulation(Simulation sim) => Sim = sim;

    public void Dispose()
    {
        _stop = true;
        try { _thread.Join(400); } catch (ThreadStateException) { /* 已经退出了 */ }
    }
}
