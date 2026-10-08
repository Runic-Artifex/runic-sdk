namespace Comparison.RoutingStateApp.S1;

// App-defined: RoutingState has no departure guard.
public interface ILeaveGuard
{
    Task<bool> CanLeaveAsync();
}
