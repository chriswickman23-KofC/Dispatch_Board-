namespace Dispatch.Shared;

public enum DriverStatus { Offline, Available, Busy }
public enum JobType { OnDemand, Scheduled }
public enum JobStatus { Unassigned, Assigned, InProgress, Delivered, Failed, Cancelled }
public enum JobSource { Manual, Csv, Api }
public enum StopKind { Pickup, Dropoff }
public enum StopStatus { Pending, Arrived, Completed, Failed }

public static class Roles
{
    public const string Admin = "Admin";
    public const string Dispatcher = "Dispatcher";
    public const string Driver = "Driver";
    public const string DispatchStaff = "Admin,Dispatcher";
}
