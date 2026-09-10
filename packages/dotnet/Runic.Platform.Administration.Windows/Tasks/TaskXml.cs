using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Tasks;

internal static class TaskXml
{
    internal static readonly XNamespace Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static XElement Element(string name, object? value) => new(Namespace + name, value);

    internal static XDocument Parse(string xml)
    {
        NativeError.Text(xml, nameof(xml));
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != Namespace + "Task") throw new ArgumentException("Expected Task Scheduler XML.", nameof(xml));
        return document;
    }

    internal static string Create(ScheduledTaskSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(specification.Principal);
        NativeError.Text(specification.Principal.Identity, nameof(specification.Principal));
        if (!Enum.IsDefined(specification.Principal.LogonType)) throw new ArgumentOutOfRangeException(nameof(specification));
        var principal = new XElement(Namespace + "Principal", new XAttribute("id", "Runic"),
            Element(specification.Principal.LogonType == TaskLogonType.Group ? "GroupId" : "UserId", specification.Principal.Identity),
            Element("RunLevel", specification.Principal.HighestPrivileges ? "HighestAvailable" : "LeastPrivilege"));
        if (specification.Principal.LogonType is not (TaskLogonType.Group or TaskLogonType.ServiceAccount))
            principal.Add(Element("LogonType", LogonName(specification.Principal.LogonType)));
        return new XDocument(new XElement(Namespace + "Task", new XAttribute("version", "1.4"),
            Element("RegistrationInfo", Element("Description", specification.Description)),
            Triggers(specification.Triggers),
            Element("Principals", principal),
            Element("Settings", new object[] {
                Element("Enabled", specification.Enabled), Element("StartWhenAvailable", specification.StartWhenAvailable),
                Element("WakeToRun", specification.WakeToRun), Element("Hidden", specification.Hidden),
                Element("ExecutionTimeLimit", Duration(specification.ExecutionTimeLimit)),
                Element("MultipleInstancesPolicy", "IgnoreNew")
            }),
            Actions(specification.Actions))).ToString(SaveOptions.DisableFormatting);
    }

    private static string LogonName(TaskLogonType value) => value switch
    {
        TaskLogonType.ServiceForUser => "S4U",
        TaskLogonType.Password => "Password",
        TaskLogonType.InteractiveTokenOrPassword => "InteractiveTokenOrPassword",
        TaskLogonType.InteractiveToken => "InteractiveToken",

        _ => throw new ArgumentException("Typed task creation requires a concrete logon identity.")
    };

    internal static string Update(string xml, ScheduledTaskUpdate update)
    {
        var document = Parse(xml);
        var root = document.Root!;
        if (update.Description is not null) Set(root, "RegistrationInfo", "Description", update.Description);
        if (update.Enabled is { } enabled) Set(root, "Settings", "Enabled", XmlConvert.ToString(enabled));
        if (update.ExecutionTimeLimit is { } limit) Set(root, "Settings", "ExecutionTimeLimit", Duration(limit));
        if (update.Actions is { } actions)
        {
            var replacement = Actions(actions);
            var existing = root.Element(Namespace + "Actions");
            replacement.SetAttributeValue("Context", existing?.Attribute("Context")?.Value);
            if (existing is null) root.Add(replacement); else existing.ReplaceWith(replacement);
        }
        if (update.Triggers is { } triggers)
        {
            var existing = root.Element(Namespace + "Triggers");
            if (existing is null) root.Add(Triggers(triggers)); else existing.ReplaceWith(Triggers(triggers));
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void Set(XElement root, string parentName, string name, string value)
    {
        var parent = root.Element(Namespace + parentName);
        if (parent is null) { parent = Element(parentName, null); root.Add(parent); }
        parent.SetElementValue(Namespace + name, value);
    }

    private static XElement Actions(ImmutableArray<TaskExecutableAction> actions)
    {
        if (actions.IsDefaultOrEmpty) throw new ArgumentException("At least one executable action is required.", nameof(actions));
        return new XElement(Namespace + "Actions", new XAttribute("Context", "Runic"), actions.Select(action =>
        {
            ArgumentNullException.ThrowIfNull(action);
            NativeError.Text(action.Path, nameof(actions));
            NativeError.Text(action.Arguments, nameof(actions), true);
            NativeError.Text(action.WorkingDirectory, nameof(actions), true);
            return Element("Exec", new object?[] { Element("Command", action.Path), Element("Arguments", action.Arguments), action.WorkingDirectory.Length == 0 ? null : Element("WorkingDirectory", action.WorkingDirectory) });
        }));
    }

    private static string Duration(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
        return XmlConvert.ToString(value);
    }

    private static XElement Triggers(ImmutableArray<TaskTrigger> triggers)
    {
        if (triggers.IsDefault) throw new ArgumentException("Triggers must be initialized.", nameof(triggers));
        return Element("Triggers", triggers.Select(Trigger));
    }

    private static XElement Trigger(TaskTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var name = trigger switch
        {
            TimeTaskTrigger => "TimeTrigger",
            DailyTaskTrigger or WeeklyTaskTrigger or MonthlyTaskTrigger => "CalendarTrigger",
            BootTaskTrigger => "BootTrigger",
            LogonTaskTrigger => "LogonTrigger",
            IdleTaskTrigger => "IdleTrigger",
            RegistrationTaskTrigger => "RegistrationTrigger",
            EventTaskTrigger => "EventTrigger",
            SessionStateTaskTrigger => "SessionStateChangeTrigger",
            _ => throw new ArgumentException("Unsupported typed trigger.", nameof(trigger))
        };
        var element = Element(name, Element("Enabled", trigger.Enabled));
        if (trigger.StartBoundary is { } start) element.Add(Element("StartBoundary", Boundary(start)));
        else if (trigger is TimeTaskTrigger or DailyTaskTrigger or WeeklyTaskTrigger or MonthlyTaskTrigger)
            throw new ArgumentException("Time and calendar triggers require a start boundary.", nameof(trigger));
        if (trigger.EndBoundary is { } end) element.Add(Element("EndBoundary", Boundary(end)));

        switch (trigger)
        {
            case DailyTaskTrigger daily:
                if (daily.DaysInterval is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(trigger));
                element.Add(Element("ScheduleByDay", Element("DaysInterval", daily.DaysInterval))); break;
            case WeeklyTaskTrigger weekly:
                if (weekly.WeeksInterval is < 1 or > 52 || weekly.Days.IsDefaultOrEmpty || weekly.Days.Any(day => !Enum.IsDefined(day)))
                    throw new ArgumentOutOfRangeException(nameof(trigger));
                element.Add(Element("ScheduleByWeek", new object[] { Element("WeeksInterval", weekly.WeeksInterval),
                    Element("DaysOfWeek", weekly.Days.Distinct().Select(day => Element(day.ToString(), null))) })); break;
            case MonthlyTaskTrigger monthly:
                if (monthly.Months.IsDefaultOrEmpty || monthly.Months.Any(month => month is < 1 or > 12) ||
                    monthly.Days.IsDefault || monthly.Days.Any(day => day is < 1 or > 31) || (monthly.Days.IsEmpty && !monthly.LastDay))
                    throw new ArgumentOutOfRangeException(nameof(trigger));
                var days = Element("DaysOfMonth", monthly.Days.Distinct().Select(day => Element("Day", day)));
                if (monthly.LastDay) days.Add(Element("Day", "Last"));
                element.Add(Element("ScheduleByMonth", new object[] { days, Element("Months", monthly.Months.Distinct().Select(month => Element(CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month), null))) })); break;
            case BootTaskTrigger boot: element.Add(Element("Delay", Duration(boot.Delay))); break;
            case LogonTaskTrigger logon:
                if (logon.UserId is not null) element.Add(Element("UserId", logon.UserId));
                element.Add(Element("Delay", Duration(logon.Delay))); break;
            case RegistrationTaskTrigger registration: element.Add(Element("Delay", Duration(registration.Delay))); break;
            case EventTaskTrigger @event:
                NativeError.Text(@event.Subscription, nameof(trigger));
                element.Add(Element("Subscription", @event.Subscription), Element("Delay", Duration(@event.Delay))); break;
            case SessionStateTaskTrigger session:
                if (!Enum.IsDefined(session.Change)) throw new ArgumentOutOfRangeException(nameof(trigger));

                if (session.UserId is not null) element.Add(Element("UserId", session.UserId));
                element.Add(Element("Delay", Duration(session.Delay)), Element("StateChange", session.Change.ToString())); break;
        }
        return element;
    }

    private static string Boundary(string value)
    {
        _ = XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.RoundtripKind);
        return value;
    }
}
