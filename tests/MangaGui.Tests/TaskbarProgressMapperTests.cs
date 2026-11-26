using MangaGui.Models;
using MangaGui.Views;
using Wpf.Ui.TaskBar;
using Xunit;

namespace MangaGui.Tests;

/// <summary>
/// Spec 10.2 table, top-down first match: Deploying -> Busy/Draining+active task -> paused -> error -> none.
/// </summary>
public sealed class TaskbarProgressMapperTests
{
    [Fact]
    public void Deploying_MapsToNormalWithDeployPercent()
    {
        var (state, value) = TaskbarProgressMapper.Map(
            SessionState.Deploying, hasActiveVm: true, isQueuePaused: false, deployPercent: 42, activeTaskPercent: null);
        Assert.Equal(TaskBarProgressState.Normal, state);
        Assert.Equal(42, value);
    }

    [Fact]
    public void Deploying_PercentIsClamped()
    {
        var (_, value) = TaskbarProgressMapper.Map(
            SessionState.Deploying, hasActiveVm: true, isQueuePaused: false, deployPercent: 130, activeTaskPercent: null);
        Assert.Equal(100, value);
    }

    [Fact]
    public void Busy_WithActiveTask_MapsToNormalWithTaskPercent()
    {
        var (state, value) = TaskbarProgressMapper.Map(
            SessionState.Busy, hasActiveVm: true, isQueuePaused: false, deployPercent: 0, activeTaskPercent: 37);
        Assert.Equal(TaskBarProgressState.Normal, state);
        Assert.Equal(37, value);
    }

    [Fact]
    public void Draining_WithActiveTask_MapsToNormalWithTaskPercent()
    {
        var (state, value) = TaskbarProgressMapper.Map(
            SessionState.Draining, hasActiveVm: true, isQueuePaused: false, deployPercent: 0, activeTaskPercent: 88);
        Assert.Equal(TaskBarProgressState.Normal, state);
        Assert.Equal(88, value);
    }

    [Fact]
    public void Busy_WithoutActiveTask_FallsThroughToNone()
    {
        var (state, _) = TaskbarProgressMapper.Map(
            SessionState.Busy, hasActiveVm: true, isQueuePaused: false, deployPercent: 0, activeTaskPercent: null);
        Assert.Equal(TaskBarProgressState.None, state);
    }

    [Fact]
    public void PausedQueue_WithActiveVm_MapsToPaused()
    {
        var (state, _) = TaskbarProgressMapper.Map(
            SessionState.Ready, hasActiveVm: true, isQueuePaused: true, deployPercent: 0, activeTaskPercent: null);
        Assert.Equal(TaskBarProgressState.Paused, state);
    }

    [Fact]
    public void PausedQueue_WithoutActiveVm_IsNone()
    {
        var (state, _) = TaskbarProgressMapper.Map(
            SessionState.Idle, hasActiveVm: false, isQueuePaused: true, deployPercent: 0, activeTaskPercent: null);
        Assert.Equal(TaskBarProgressState.None, state);
    }

    [Fact]
    public void Error_WithActiveVm_MapsToError()
    {
        var (state, _) = TaskbarProgressMapper.Map(
            SessionState.Error, hasActiveVm: true, isQueuePaused: false, deployPercent: 0, activeTaskPercent: null);
        Assert.Equal(TaskBarProgressState.Error, state);
    }

    [Fact]
    public void Error_WithoutActiveVm_IsNone()
    {
        var (state, _) = TaskbarProgressMapper.Map(
            SessionState.Error, hasActiveVm: false, isQueuePaused: false, deployPercent: 0, activeTaskPercent: null);
        Assert.Equal(TaskBarProgressState.None, state);
    }

    [Fact]
    public void ErrorAndPaused_WithActiveVm_PausedWinsByTableOrder()
    {
        // Row order in spec 10.2: the paused row sits above the error row.
        var (state, _) = TaskbarProgressMapper.Map(
            SessionState.Error, hasActiveVm: true, isQueuePaused: true, deployPercent: 0, activeTaskPercent: null);
        Assert.Equal(TaskBarProgressState.Paused, state);
    }

    [Fact]
    public void Deploying_WinsOverPausedAndError()
    {
        var (state, value) = TaskbarProgressMapper.Map(
            SessionState.Deploying, hasActiveVm: true, isQueuePaused: true, deployPercent: 55, activeTaskPercent: 10);
        Assert.Equal(TaskBarProgressState.Normal, state);
        Assert.Equal(55, value);
    }

    [Fact]
    public void Idle_IsNone()
    {
        var (state, value) = TaskbarProgressMapper.Map(
            SessionState.Idle, hasActiveVm: false, isQueuePaused: false, deployPercent: 0, activeTaskPercent: null);
        Assert.Equal(TaskBarProgressState.None, state);
        Assert.Equal(0, value);
    }
}
