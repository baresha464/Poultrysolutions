namespace AmrPoultryFarmWeb.Models;

/// <summary>
/// Code-defined catalog of permission codes, grouped by module. Roles (data) are granted a
/// subset of these codes; the codes themselves only ever change when a developer adds a
/// new page/action, same as ABP's permission-definition-provider pattern.
/// </summary>
public static class Permissions
{
    public static class Houses
    {
        public const string View = "Houses.View";
        public const string Add = "Houses.Add";
        public const string Edit = "Houses.Edit";
        public const string Delete = "Houses.Delete";
    }

    public static class Batches
    {
        public const string View = "Batches.View";
        public const string Add = "Batches.Add";
        public const string Edit = "Batches.Edit";
        public const string Delete = "Batches.Delete";
    }

    public static class DailyRecords
    {
        public const string View = "DailyRecords.View";
        public const string Add = "DailyRecords.Add";
        public const string Edit = "DailyRecords.Edit";
        public const string Delete = "DailyRecords.Delete";
    }

    public static class Feed
    {
        public const string View = "Feed.View";
        public const string Add = "Feed.Add";
        public const string Edit = "Feed.Edit";
        public const string Delete = "Feed.Delete";
    }

    public static class Health
    {
        public const string View = "Health.View";
        public const string Add = "Health.Add";
        public const string Edit = "Health.Edit";
        public const string Delete = "Health.Delete";
    }

    public static class Lifting
    {
        public const string View = "Lifting.View";
        public const string Add = "Lifting.Add";
        public const string Edit = "Lifting.Edit";
        public const string Delete = "Lifting.Delete";
    }

    public static class Settlement
    {
        public const string View = "Settlement.View";
        public const string Add = "Settlement.Add";
        public const string Edit = "Settlement.Edit";
    }

    public static class Expenses
    {
        public const string View = "Expenses.View";
        public const string Add = "Expenses.Add";
        public const string Edit = "Expenses.Edit";
        public const string Delete = "Expenses.Delete";
    }

    public static class Reports
    {
        public const string View = "Reports.View";
    }

    public static class Integrators
    {
        public const string View = "Integrators.View";
        public const string Add = "Integrators.Add";
        public const string Edit = "Integrators.Edit";
        public const string Delete = "Integrators.Delete";
    }

    public static class Users
    {
        public const string View = "Users.View";
        public const string Add = "Users.Add";
        public const string Edit = "Users.Edit";
        public const string Delete = "Users.Delete";
    }

    public static class Roles
    {
        public const string View = "Roles.View";
        public const string Add = "Roles.Add";
        public const string Edit = "Roles.Edit";
        public const string Delete = "Roles.Delete";
    }
}

/// <summary>Reflection-free listing of every permission code, grouped for the role-editor checkbox grid.</summary>
public static class PermissionCatalog
{
    public static readonly List<(string Group, List<(string Code, string Label)> Items)> All = new()
    {
        ("Houses", new() { ("Houses.View", "View"), ("Houses.Add", "Add"), ("Houses.Edit", "Edit"), ("Houses.Delete", "Delete") }),
        ("Batches", new() { ("Batches.View", "View"), ("Batches.Add", "Add"), ("Batches.Edit", "Edit"), ("Batches.Delete", "Delete") }),
        ("Daily Records", new() { ("DailyRecords.View", "View"), ("DailyRecords.Add", "Add"), ("DailyRecords.Edit", "Edit"), ("DailyRecords.Delete", "Delete") }),
        ("Feed", new() { ("Feed.View", "View"), ("Feed.Add", "Add"), ("Feed.Edit", "Edit"), ("Feed.Delete", "Delete") }),
        ("Health", new() { ("Health.View", "View"), ("Health.Add", "Add"), ("Health.Edit", "Edit"), ("Health.Delete", "Delete") }),
        ("Lifting", new() { ("Lifting.View", "View"), ("Lifting.Add", "Add"), ("Lifting.Edit", "Edit"), ("Lifting.Delete", "Delete") }),
        ("Settlement", new() { ("Settlement.View", "View"), ("Settlement.Add", "Add"), ("Settlement.Edit", "Edit") }),
        ("Expenses", new() { ("Expenses.View", "View"), ("Expenses.Add", "Add"), ("Expenses.Edit", "Edit"), ("Expenses.Delete", "Delete") }),
        ("Reports", new() { ("Reports.View", "View") }),
        ("Integrators", new() { ("Integrators.View", "View"), ("Integrators.Add", "Add"), ("Integrators.Edit", "Edit"), ("Integrators.Delete", "Delete") }),
        ("Users", new() { ("Users.View", "View"), ("Users.Add", "Add"), ("Users.Edit", "Edit"), ("Users.Delete", "Delete") }),
        ("Roles", new() { ("Roles.View", "View"), ("Roles.Add", "Add"), ("Roles.Edit", "Edit"), ("Roles.Delete", "Delete") }),
    };

    public static IEnumerable<string> AllCodes => All.SelectMany(g => g.Items.Select(i => i.Code));
}
