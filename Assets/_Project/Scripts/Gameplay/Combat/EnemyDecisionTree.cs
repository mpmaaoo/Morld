using System;

/// <summary>Reactive selector: higher priority conditions are evaluated again every tick.</summary>
public static class EnemyDecisionTree
{
    public enum Result { Failure, Success, Running }
    public interface INode { Result Tick(); }
    public sealed class Condition : INode
    {
        private readonly Func<bool> test;
        public Condition(Func<bool> test) { this.test = test; }
        public Result Tick() => test() ? Result.Success : Result.Failure;
    }
    public sealed class Action : INode
    {
        private readonly Func<Result> action;
        public Action(Func<Result> action) { this.action = action; }
        public Result Tick() => action();
    }
    public sealed class Sequence : INode
    {
        private readonly INode[] children;
        public Sequence(params INode[] children) { this.children = children; }
        public Result Tick()
        {
            foreach (var child in children) { var result = child.Tick(); if (result != Result.Success) return result; }
            return Result.Success;
        }
    }
    public sealed class Selector : INode
    {
        private readonly INode[] children;
        public Selector(params INode[] children) { this.children = children; }
        public Result Tick()
        {
            foreach (var child in children) { var result = child.Tick(); if (result != Result.Failure) return result; }
            return Result.Failure;
        }
    }
}

public static class EnemyPursuitRules
{
    public static bool ShouldPrioritizeDepth(float gap, float limit) => System.Math.Abs(gap) > limit;
    public static bool ShouldLock(bool locked, bool previouslyDetected, bool alive, float distance, float radius)
        => alive && radius > 0f && distance <= radius && (locked || previouslyDetected);
}
