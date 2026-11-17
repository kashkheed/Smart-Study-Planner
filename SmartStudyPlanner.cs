// =====================================================================================
//  MOD002702 Software Implementation 2026-27  -  Smart Study Planner (Part II)
//  Console application written in C#.
//
//  Overview of the design (see the report for the UML diagram):
//    Priority / StudyStatus   - enums describing a task
//    StudyTask                - one study task (title, description, due date, hours, ...)
//    Module                   - a module (code + name) that owns a list of StudyTask objects
//    StudyPlanner             - the "model": holds all modules and implements the business logic
//    Validation               - static helpers that validate and parse raw text (used by UI and file loader)
//    InputHelper              - static helpers that repeatedly prompt until valid input is entered
//    DataStore                - file I/O (CSV): save with temp-file + backup, load with corruption handling
//    IRecommender             - interface for recommendation strategies (OPTIONAL advanced feature)
//    ScoreBasedRecommender    - rule/score based implementation of IRecommender (OPTIONAL feature)
//    ConsoleUI                - the menu system that ties everything together
//    Program                  - entry point
//
//  Language level: C# 7.x compatible (no records / switch expressions), so it compiles in
//  Visual Studio, with "dotnet build", or with the classic csc compiler.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SmartStudyPlanner
{
    // ---------------------------------------------------------------------------------
    //  ENUMS
    // ---------------------------------------------------------------------------------

    /// <summary>Priority level of a study task. Numeric values are used for sorting/scoring.</summary>
    public enum Priority
    {
        Low = 1,
        Medium = 2,
        High = 3
    }

    /// <summary>Progress status of a study task.</summary>
    public enum StudyStatus
    {
        NotStarted = 0,
        InProgress = 1,
        Completed = 2
    }

    // ---------------------------------------------------------------------------------
    //  DATA CLASSES
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Represents a single study task (requirement 1). A task always belongs to exactly one Module;
    /// ModuleCode is stored on the task so it can be displayed and saved without searching.
    /// </summary>
    public class StudyTask
    {
        public int Id { get; set; }                 // unique identifier used to select tasks in the UI
        public string ModuleCode { get; set; }      // code of the owning module
        public string Title { get; set; }           // study task title
        public string Description { get; set; }     // optional free text
        public DateTime DueDate { get; set; }       // due date (time part is ignored)
        public double EstimatedHours { get; set; }  // estimated study time in hours
        public Priority Priority { get; set; }
        public StudyStatus Status { get; set; }

        /// <summary>True when the task has not been completed yet.</summary>
        public bool IsOutstanding
        {
            get { return Status != StudyStatus.Completed; }
        }

        /// <summary>Number of days from 'today' until the due date (negative = overdue).</summary>
        public int DaysUntilDue(DateTime today)
        {
            return (DueDate.Date - today.Date).Days;
        }

        /// <summary>Human readable status text with spaces, e.g. "Not Started".</summary>
        public string StatusText
        {
            get { return Validation.StatusToText(Status); }
        }
    }

    /// <summary>A module (e.g. "MOD002702 - Software Implementation") and its associated study tasks.</summary>
    public class Module
    {
        public string Code { get; private set; }
        public string Name { get; private set; }
        public List<StudyTask> Tasks { get; private set; }   // generic collection of tasks for this module

        public Module(string code, string name)
        {
            Code = code;
            Name = name;
            Tasks = new List<StudyTask>();
        }
    }

    // ---------------------------------------------------------------------------------
    //  VALIDATION HELPERS  (requirement 4)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Pure validation/parsing functions. They never prompt the user, so they can be reused by the
    /// console input code and by the file loader (which must validate data read from disk too).
    /// </summary>
    public static class Validation
    {
        // Accepted date formats. Parsing is done with the invariant culture so behaviour does not
        // depend on the regional settings of the machine.
        private static readonly string[] DateFormats =
            { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy" };

        public const double MaxHours = 200;
        public const int MaxTitleLength = 60;
        public const int MaxModuleNameLength = 60;
        public const int MaxDescriptionLength = 200;

        /// <summary>Module codes: 2-12 characters, letters/digits/hyphen/underscore only.</summary>
        public static bool IsValidModuleCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            code = code.Trim();
            if (code.Length < 2 || code.Length > 12) return false;
            return code.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
        }

        /// <summary>Parses a date, rejecting wrong formats and unrealistic years.</summary>
        public static bool TryParseDate(string text, out DateTime date, out string error)
        {
            error = null;
            date = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Date cannot be empty. Use yyyy-MM-dd (e.g. 2026-12-11) or dd/MM/yyyy.";
                return false;
            }
            if (!DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out date))
            {
                error = "'" + text + "' is not a valid date. Use yyyy-MM-dd (e.g. 2026-12-11) or dd/MM/yyyy, and a real calendar date.";
                return false;
            }
            if (date.Year < 2000 || date.Year > 2100)
            {
                error = "Date is out of the supported range (year 2000 - 2100).";
                return false;
            }
            return true;
        }

        /// <summary>Parses estimated study time in hours: must be a number > 0 and at most MaxHours.</summary>
        public static bool TryParseHours(string text, out double hours, out string error)
        {
            error = null;
            hours = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Estimated study time cannot be empty. Enter a number of hours, e.g. 2 or 1.5.";
                return false;
            }
            // Allow a decimal comma ("1,5") as well as a decimal point.
            string cleaned = text.Trim().Replace(',', '.');
            if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out hours)
                || double.IsNaN(hours) || double.IsInfinity(hours))
            {
                error = "'" + text + "' is not a number. Enter hours such as 2 or 1.5.";
                return false;
            }
            if (hours <= 0)
            {
                error = "Estimated study time must be greater than zero.";
                return false;
            }
            if (hours > MaxHours)
            {
                error = "Estimated study time must not exceed " + MaxHours + " hours.";
                return false;
            }
            return true;
        }

        /// <summary>Accepts Low/Medium/High (any case), L/M/H, or 1/2/3. Anything else is rejected.</summary>
        public static bool TryParsePriority(string text, out Priority priority)
        {
            priority = Priority.Medium;
            if (text == null) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "low": case "l": case "1": priority = Priority.Low; return true;
                case "medium": case "med": case "m": case "2": priority = Priority.Medium; return true;
                case "high": case "h": case "3": priority = Priority.High; return true;
                default: return false;
            }
        }

        /// <summary>Accepts "Not Started"/"NotStarted"/"In Progress"/"Completed", short forms, or 1/2/3.</summary>
        public static bool TryParseStatus(string text, out StudyStatus status)
        {
            status = StudyStatus.NotStarted;
            if (text == null) return false;
            string t = text.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
            switch (t)
            {
                case "notstarted": case "ns": case "n": case "1": status = StudyStatus.NotStarted; return true;
                case "inprogress": case "ip": case "p": case "2": status = StudyStatus.InProgress; return true;
                case "completed": case "complete": case "done": case "c": case "3": status = StudyStatus.Completed; return true;
                default: return false;
            }
        }

        public static string StatusToText(StudyStatus s)
        {
            switch (s)
            {
                case StudyStatus.NotStarted: return "Not Started";
                case StudyStatus.InProgress: return "In Progress";
                default: return "Completed";
            }
        }
    }

    // ---------------------------------------------------------------------------------
    //  STUDY PLANNER (business logic / model)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Holds all modules and tasks and implements the study-management features (requirement 3).
    /// The UI never edits the collections directly; it calls these methods so that rules
    /// (unique module codes, unique task IDs, valid data) are enforced in one place.
    /// </summary>
    public class StudyPlanner
    {
        /// <summary>A task is "due soon" if it is outstanding and due within this many days (and not overdue).</summary>
        public const int DueSoonDays = 3;

        private List<Module> modules = new List<Module>();
        private int nextTaskId = 1;

        /// <summary>True when there are changes that have not been saved to a file.</summary>
        public bool IsDirty { get; set; }

        public IReadOnlyList<Module> Modules
        {
            get { return modules; }
        }

        // ----- Modules -----

        public Module FindModule(string code)
        {
            if (code == null) return null;
            return modules.FirstOrDefault(m => string.Equals(m.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public Module AddModule(string code, string name)
        {
            if (!Validation.IsValidModuleCode(code))
                throw new ArgumentException("Module code must be 2-12 letters, digits, '-' or '_'.");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Module name cannot be empty.");
            if (FindModule(code) != null)
                throw new ArgumentException("A module with code '" + code.Trim().ToUpperInvariant() + "' already exists.");

            var m = new Module(code.Trim().ToUpperInvariant(), name.Trim());
            modules.Add(m);
            IsDirty = true;
            return m;
        }

        // ----- Tasks -----

        /// <summary>All tasks across all modules (LINQ SelectMany flattens the per-module lists).</summary>
        public IEnumerable<StudyTask> AllTasks()
        {
            return modules.SelectMany(m => m.Tasks);
        }

        public StudyTask FindTask(int id)
        {
            return AllTasks().FirstOrDefault(t => t.Id == id);
        }

        public StudyTask AddTask(string moduleCode, string title, string description, DateTime due,
                                 double hours, Priority priority, StudyStatus status)
        {
            Module m = FindModule(moduleCode);
            if (m == null) throw new ArgumentException("Module '" + moduleCode + "' does not exist.");
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Task title cannot be empty.");
            if (hours <= 0 || hours > Validation.MaxHours) throw new ArgumentException("Invalid estimated study time.");

            var task = new StudyTask
            {
                Id = nextTaskId++,
                ModuleCode = m.Code,
                Title = title.Trim(),
                Description = (description ?? "").Trim(),
                DueDate = due.Date,
                EstimatedHours = hours,
                Priority = priority,
                Status = status
            };
            m.Tasks.Add(task);
            IsDirty = true;
            return task;
        }

        public bool DeleteTask(int id)
        {
            foreach (Module m in modules)
            {
                StudyTask t = m.Tasks.FirstOrDefault(x => x.Id == id);
                if (t != null)
                {
                    m.Tasks.Remove(t);
                    IsDirty = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Assigns (moves) an existing task to a different module.</summary>
        public void MoveTask(StudyTask task, string newModuleCode)
        {
            Module target = FindModule(newModuleCode);
            if (target == null) throw new ArgumentException("Module '" + newModuleCode + "' does not exist.");
            Module current = FindModule(task.ModuleCode);
            if (current == target) return;                // nothing to do
            if (current != null) current.Tasks.Remove(task);
            target.Tasks.Add(task);
            task.ModuleCode = target.Code;
            IsDirty = true;
        }

        public void MarkCompleted(StudyTask task)
        {
            task.Status = StudyStatus.Completed;
            IsDirty = true;
        }

        // ----- Queries (LINQ) -----

        public IEnumerable<StudyTask> Outstanding()
        {
            return AllTasks().Where(t => t.IsOutstanding);
        }

        public IEnumerable<StudyTask> Completed()
        {
            return AllTasks().Where(t => !t.IsOutstanding);
        }

        /// <summary>Outstanding tasks that are overdue or due within DueSoonDays, earliest first.</summary>
        public IEnumerable<StudyTask> DueSoon(DateTime today)
        {
            return Outstanding()
                   .Where(t => t.DaysUntilDue(today) <= DueSoonDays)
                   .OrderBy(t => t.DueDate)
                   .ThenByDescending(t => t.Priority);
        }

        /// <summary>Total estimated hours of all tasks that are not yet completed.</summary>
        public double TotalOutstandingHours()
        {
            return Outstanding().Sum(t => t.EstimatedHours);
        }

        public IEnumerable<StudyTask> FilterByModule(string code)
        {
            return AllTasks().Where(t => string.Equals(t.ModuleCode, code, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<StudyTask> FilterByStatus(StudyStatus status)
        {
            return AllTasks().Where(t => t.Status == status);
        }

        // ----- Loading support -----

        /// <summary>Replaces all data (used after a successful load) and recalculates the next task id.</summary>
        public void ReplaceData(List<Module> newModules)
        {
            modules = newModules;
            int max = AllTasks().Select(t => t.Id).DefaultIfEmpty(0).Max();
            nextTaskId = max + 1;
            IsDirty = false;
        }
    }

    // ---------------------------------------------------------------------------------
    //  FILE I/O  (requirement 5)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Saves/loads the planner to a CSV text file. File layout:
    ///   SSP1                                              (header / format version)
    ///   M,moduleCode,moduleName                           (one line per module)
    ///   T,moduleCode,id,title,description,yyyy-MM-dd,hours,priority,status   (one line per task)
    /// Fields containing commas or quotes are wrapped in quotes (standard CSV escaping).
    /// </summary>
    public static class DataStore
    {
        private const string Header = "SSP1";

        /// <summary>
        /// Writes the data to a temporary file first, then copies it over the target. If anything fails
        /// part-way through, the original file is left intact (data consistency). An existing file is
        /// also copied to ".bak" before being replaced.
        /// </summary>
        public static void Save(StudyPlanner planner, string path)
        {
            var lines = new List<string>();
            lines.Add(Header);
            foreach (Module m in planner.Modules)
                lines.Add("M," + Csv(m.Code) + "," + Csv(m.Name));
            foreach (Module m in planner.Modules)
                foreach (StudyTask t in m.Tasks)
                    lines.Add(string.Join(",", new[]
                    {
                        "T", Csv(t.ModuleCode), t.Id.ToString(CultureInfo.InvariantCulture), Csv(t.Title),
                        Csv(t.Description), t.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        t.EstimatedHours.ToString("0.##", CultureInfo.InvariantCulture),
                        t.Priority.ToString(), t.Status.ToString()
                    }));

            string tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines, Encoding.UTF8);            // may throw IOException etc. (caught by caller)
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        /// <summary>
        /// Loads modules/tasks from a file. Throws FileNotFoundException if the file is missing and
        /// InvalidDataException if the header is wrong (not one of our files / corrupted). Individual bad
        /// lines are skipped and described in 'warnings' so one corrupt line does not lose all the data.
        /// Nothing is applied to the planner here - the caller does that only on success.
        /// </summary>
        public static List<Module> Load(string path, List<string> warnings)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("File not found: " + path);

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0 || lines[0].Trim() != Header)
                throw new InvalidDataException("The file is empty or is not a valid Smart Study Planner file (missing '" + Header + "' header).");

            var modules = new List<Module>();
            var usedIds = new HashSet<int>();

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                int lineNo = i + 1;
                try
                {
                    List<string> f = SplitCsv(lines[i]);
                    if (f[0] == "M")
                        ParseModule(f, modules);
                    else if (f[0] == "T")
                        ParseTask(f, modules, usedIds);
                    else
                        throw new FormatException("unknown record type '" + f[0] + "'");
                }
                catch (FormatException ex)
                {
                    warnings.Add("Line " + lineNo + " skipped: " + ex.Message);
                }
            }
            return modules;
        }

        private static void ParseModule(List<string> f, List<Module> modules)
        {
            if (f.Count != 3) throw new FormatException("module record needs 3 fields but has " + f.Count);
            if (!Validation.IsValidModuleCode(f[1])) throw new FormatException("invalid module code '" + f[1] + "'");
            if (string.IsNullOrWhiteSpace(f[2])) throw new FormatException("empty module name");
            string code = f[1].Trim().ToUpperInvariant();
            if (modules.Any(m => m.Code == code)) throw new FormatException("duplicate module '" + code + "'");
            modules.Add(new Module(code, f[2].Trim()));
        }

        private static void ParseTask(List<string> f, List<Module> modules, HashSet<int> usedIds)
        {
            if (f.Count != 9) throw new FormatException("task record needs 9 fields but has " + f.Count);

            string code = f[1].Trim().ToUpperInvariant();
            Module m = modules.FirstOrDefault(x => x.Code == code);
            if (m == null) throw new FormatException("task refers to unknown module '" + f[1] + "'");

            int id;
            if (!int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id <= 0)
                throw new FormatException("invalid task id '" + f[2] + "'");
            if (!usedIds.Add(id)) throw new FormatException("duplicate task id " + id);

            if (string.IsNullOrWhiteSpace(f[3])) throw new FormatException("empty task title");

            DateTime due; string err;
            if (!Validation.TryParseDate(f[5], out due, out err)) throw new FormatException(err);
            double hours;
            if (!Validation.TryParseHours(f[6], out hours, out err)) throw new FormatException(err);
            Priority pr;
            if (!Validation.TryParsePriority(f[7], out pr)) throw new FormatException("invalid priority '" + f[7] + "'");
            StudyStatus st;
            if (!Validation.TryParseStatus(f[8], out st)) throw new FormatException("invalid status '" + f[8] + "'");

            m.Tasks.Add(new StudyTask
            {
                Id = id, ModuleCode = m.Code, Title = f[3].Trim(), Description = f[4].Trim(),
                DueDate = due.Date, EstimatedHours = hours, Priority = pr, Status = st
            });
        }

        /// <summary>Escapes one CSV field (quotes it when it contains a comma, quote or line break).</summary>
        private static string Csv(string value)
        {
            if (value == null) return "";
            value = value.Replace("\r", " ").Replace("\n", " ");
            if (value.Contains(",") || value.Contains("\""))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        /// <summary>Splits one CSV line into fields, honouring quoted fields and doubled quotes.</summary>
        private static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }   // escaped quote
                        else inQuotes = false;                                                    // closing quote
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            if (inQuotes) throw new FormatException("unterminated quoted field");
            fields.Add(sb.ToString());
            return fields;
        }
    }

    // ---------------------------------------------------------------------------------
    //  SMART STUDY RECOMMENDATION  (requirement 8 - optional, plus interface for requirement 9)
    // ---------------------------------------------------------------------------------

    /// <summary>The result of ranking one task: the task, its score and a short explanation.</summary>
    public class Recommendation
    {
        public StudyTask Task { get; set; }
        public int Score { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Abstraction for recommendation strategies. The UI depends only on this interface, so a different
    /// strategy (e.g. one that weights estimated time differently) can be swapped in without UI changes.
    /// </summary>
    public interface IRecommender
    {
        /// <summary>Returns outstanding tasks ranked best-first, each with a score and explanation.</summary>
        List<Recommendation> Rank(IEnumerable<StudyTask> tasks, DateTime today);
    }

    /// <summary>
    /// Simple additive scoring model. Points are awarded for:
    ///   urgency  - overdue (60+), due today (55), within 3 days (40), 7 days (25), 14 days (10)
    ///   priority - High 30, Medium 15, Low 5
    ///   status   - In Progress +10 (finishing started work avoids wasted context switching)
    ///   effort   - tasks of 2 hours or less +5 (quick wins)
    /// Completed tasks are never recommended. The reasons that earned points form the explanation.
    /// </summary>
    public class ScoreBasedRecommender : IRecommender
    {
        public List<Recommendation> Rank(IEnumerable<StudyTask> tasks, DateTime today)
        {
            return tasks
                .Where(t => t.IsOutstanding)
                .Select(t => Evaluate(t, today))
                .OrderByDescending(r => r.Score)                 // best score first
                .ThenBy(r => r.Task.DueDate)                     // tie-break 1: earlier due date
                .ThenBy(r => r.Task.EstimatedHours)              // tie-break 2: shorter task
                .ToList();
        }

        private Recommendation Evaluate(StudyTask t, DateTime today)
        {
            int score = 0;
            var reasons = new List<string>();
            int days = t.DaysUntilDue(today);

            // ----- Urgency -----
            if (days < 0)
            {
                score += 60 + Math.Min(-days, 10);               // the longer overdue, the higher (capped)
                reasons.Add("overdue by " + (-days) + (days == -1 ? " day" : " days"));
            }
            else if (days == 0) { score += 55; reasons.Add("due today"); }
            else if (days <= 3) { score += 40; reasons.Add("due within " + days + (days == 1 ? " day" : " days")); }
            else if (days <= 7) { score += 25; reasons.Add("due in " + days + " days"); }
            else if (days <= 14) { score += 10; reasons.Add("due in " + days + " days"); }

            // ----- Priority -----
            switch (t.Priority)
            {
                case Priority.High: score += 30; reasons.Add("high priority"); break;
                case Priority.Medium: score += 15; reasons.Add("medium priority"); break;
                default: score += 5; break;                      // low priority is not worth mentioning
            }

            // ----- Status -----
            if (t.Status == StudyStatus.InProgress) { score += 10; reasons.Add("already in progress"); }

            // ----- Effort -----
            if (t.EstimatedHours <= 2) { score += 5; reasons.Add("short task (" + t.EstimatedHours.ToString("0.##", CultureInfo.InvariantCulture) + "h) that can be finished quickly"); }

            if (reasons.Count == 0) reasons.Add("no urgent deadline, so this is the next-best use of your time");

            return new Recommendation { Task = t, Score = score, Reason = JoinReasons(reasons) };
        }

        /// <summary>Joins reasons into a sentence: "a, b and c" with a capital first letter.</summary>
        private static string JoinReasons(List<string> reasons)
        {
            string text = reasons.Count == 1
                ? reasons[0]
                : string.Join(", ", reasons.Take(reasons.Count - 1)) + " and " + reasons.Last();
            return char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";
        }
    }

    // ---------------------------------------------------------------------------------
    //  INPUT HELPERS (validated console input)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Prompts for values and keeps asking until valid input is given, printing a meaningful error each
    /// time. When 'current' is supplied (edit mode / defaults) pressing Enter keeps that value.
    /// </summary>
    public static class InputHelper
    {
        /// <summary>Reads a trimmed line. Throws EndOfStreamException if input has been closed (avoids infinite loops).</summary>
        public static string ReadLine(string prompt)
        {
            Console.Write(prompt);
            string s = Console.ReadLine();
            if (s == null) throw new EndOfStreamException("Input stream closed.");
            return s.Trim();
        }

        public static void Error(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  [Error] " + message);
            Console.ResetColor();
        }

        public static void Success(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  [OK] " + message);
            Console.ResetColor();
        }

        public static void Info(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  " + message);
            Console.ResetColor();
        }

        /// <summary>Required text (module/task names). Empty input is rejected unless a current value exists.</summary>
        public static string ReadRequiredText(string label, int maxLength, string current = null)
        {
            while (true)
            {
                string s = ReadLine(current == null ? label + ": " : label + " [" + current + "]: ");
                if (s.Length == 0 && current != null) return current;
                if (s.Length == 0) { Error(label + " cannot be empty."); continue; }
                if (s.Length > maxLength) { Error(label + " must be at most " + maxLength + " characters (you entered " + s.Length + ")."); continue; }
                return s;
            }
        }

        /// <summary>Optional text. On add, Enter = empty. On edit, Enter keeps current and "-" clears it.</summary>
        public static string ReadOptionalText(string label, int maxLength, string current = null)
        {
            while (true)
            {
                string hint = current == null ? "(optional)" : "[" + (current.Length == 0 ? "empty" : current) + "] ('-' to clear)";
                string s = ReadLine(label + " " + hint + ": ");
                if (current != null)
                {
                    if (s.Length == 0) return current;
                    if (s == "-") return "";
                }
                if (s.Length > maxLength) { Error(label + " must be at most " + maxLength + " characters."); continue; }
                return s;
            }
        }

        public static string ReadModuleCode(string label, string current = null)
        {
            while (true)
            {
                string s = ReadLine(current == null ? label + ": " : label + " [" + current + "]: ");
                if (s.Length == 0 && current != null) return current;
                if (s.Length == 0) { Error(label + " cannot be empty."); continue; }
                if (!Validation.IsValidModuleCode(s)) { Error("Module code must be 2-12 characters: letters, digits, '-' or '_' only (no spaces or commas)."); continue; }
                return s.ToUpperInvariant();
            }
        }

        public static DateTime ReadDate(string label, DateTime? current = null)
        {
            while (true)
            {
                string s = ReadLine(current == null ? label + " (yyyy-MM-dd): "
                                                    : label + " [" + current.Value.ToString("yyyy-MM-dd") + "]: ");
                if (s.Length == 0 && current.HasValue) return current.Value;
                DateTime d; string err;
                if (Validation.TryParseDate(s, out d, out err)) return d.Date;
                Error(err);
            }
        }

        public static double ReadHours(string label, double? current = null)
        {
            while (true)
            {
                string s = ReadLine(current == null ? label + " (hours): "
                                                    : label + " [" + current.Value.ToString("0.##", CultureInfo.InvariantCulture) + "]: ");
                if (s.Length == 0 && current.HasValue) return current.Value;
                double h; string err;
                if (Validation.TryParseHours(s, out h, out err)) return h;
                Error(err);
            }
        }

        public static Priority ReadPriority(Priority? current = null)
        {
            while (true)
            {
                string s = ReadLine(current == null ? "Priority (Low/Medium/High): "
                                                    : "Priority (Low/Medium/High) [" + current.Value + "]: ");
                if (s.Length == 0 && current.HasValue) return current.Value;
                Priority p;
                if (Validation.TryParsePriority(s, out p)) return p;
                Error("'" + s + "' is not a valid priority. Enter Low, Medium or High (or L/M/H, 1/2/3).");
            }
        }

        public static StudyStatus ReadStatus(StudyStatus? current = null)
        {
            while (true)
            {
                string s = ReadLine(current == null ? "Status (Not Started/In Progress/Completed): "
                                                    : "Status (Not Started/In Progress/Completed) [" + Validation.StatusToText(current.Value) + "]: ");
                if (s.Length == 0 && current.HasValue) return current.Value;
                StudyStatus st;
                if (Validation.TryParseStatus(s, out st)) return st;
                Error("'" + s + "' is not a valid status. Enter Not Started, In Progress or Completed (or 1/2/3).");
            }
        }

        public static bool AskYesNo(string question)
        {
            while (true)
            {
                string s = ReadLine(question + " (y/n): ").ToLowerInvariant();
                if (s == "y" || s == "yes") return true;
                if (s == "n" || s == "no") return false;
                Error("Please answer y or n.");
            }
        }
    }

    // ---------------------------------------------------------------------------------
    //  CONSOLE USER INTERFACE  (requirements 2 and 3)
    // ---------------------------------------------------------------------------------

    /// <summary>Menu-driven console interface that calls into StudyPlanner, DataStore and IRecommender.</summary>
    public class ConsoleUI
    {
        private readonly StudyPlanner planner;
        private readonly IRecommender recommender;     // depends on the interface, not the concrete class
        private string currentFile;                    // file used for the last save/load

        private static readonly string DefaultFile =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "studyplanner.csv");

        public ConsoleUI(StudyPlanner planner, IRecommender recommender)
        {
            this.planner = planner;
            this.recommender = recommender;
            currentFile = DefaultFile;
        }

        /// <summary>Main loop. Any unexpected exception from a menu action is reported and the program continues.</summary>
        public void Run()
        {
            Console.WriteLine("==============================================");
            Console.WriteLine("            SMART STUDY PLANNER");
            Console.WriteLine("==============================================");

            try
            {
                if (File.Exists(DefaultFile) &&
                    InputHelper.AskYesNo("Saved data found (" + Path.GetFileName(DefaultFile) + "). Load it now?"))
                    LoadFromFile(DefaultFile);

                bool running = true;
                while (running)
                {
                    ShowMenu();
                    string choice = InputHelper.ReadLine("Choose an option: ");
                    try
                    {
                        running = Dispatch(choice);
                    }
                    catch (EndOfStreamException) { throw; }                 // input closed: leave the program
                    catch (Exception ex)                                    // keep the program stable (requirement 6)
                    {
                        InputHelper.Error("Unexpected problem: " + ex.Message);
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Console.WriteLine();
                Console.WriteLine("Input closed. Goodbye.");
            }
        }

        private void ShowMenu()
        {
            Console.WriteLine();
            Console.WriteLine("-------------------- MENU --------------------");
            Console.WriteLine(" 1. Add a new module");
            Console.WriteLine(" 2. Add a new study task");
            Console.WriteLine(" 3. Edit a study task");
            Console.WriteLine(" 4. Delete a study task");
            Console.WriteLine(" 5. Assign (move) a task to a module");
            Console.WriteLine(" 6. Mark a task as completed");
            Console.WriteLine(" 7. View all modules and tasks");
            Console.WriteLine(" 8. Filter tasks (by module / status)");
            Console.WriteLine(" 9. Sort tasks (by due date / priority)");
            Console.WriteLine("10. View outstanding and completed tasks");
            Console.WriteLine("11. Due-soon tasks and total outstanding study time");
            Console.WriteLine("12. Smart recommendation: what should I study next?");
            Console.WriteLine("13. Save data to file");
            Console.WriteLine("14. Load data from file");
            Console.WriteLine("15. Load sample data (for demonstration)");
            Console.WriteLine(" 0. Exit");
            Console.WriteLine("----------------------------------------------");
        }

        /// <summary>Runs the chosen menu option. Returns false when the program should exit.</summary>
        private bool Dispatch(string choice)
        {
            switch (choice)
            {
                case "1": AddModule(); break;
                case "2": AddTask(); break;
                case "3": EditTask(); break;
                case "4": DeleteTask(); break;
                case "5": AssignTask(); break;
                case "6": MarkCompleted(); break;
                case "7": ViewAll(); break;
                case "8": FilterTasks(); break;
                case "9": SortTasks(); break;
                case "10": ViewOutstandingAndCompleted(); break;
                case "11": ViewDueSoonAndTime(); break;
                case "12": ShowRecommendation(); break;
                case "13": SaveToFile(); break;
                case "14": LoadMenu(); break;
                case "15": LoadSampleData(); break;
                case "0": return !ConfirmExit();
                default: InputHelper.Error("'" + choice + "' is not a menu option. Enter a number from 0 to 15."); break;
            }
            return true;
        }

        // ----- Module & task entry -----

        private void AddModule()
        {
            Console.WriteLine("\n--- Add module ---");
            string code;
            while (true)
            {
                code = InputHelper.ReadModuleCode("Module code (e.g. MOD002702)");
                if (planner.FindModule(code) != null) { InputHelper.Error("Module '" + code + "' already exists."); continue; }
                break;
            }
            string name = InputHelper.ReadRequiredText("Module name", Validation.MaxModuleNameLength);
            Module m = planner.AddModule(code, name);
            InputHelper.Success("Module added: " + m.Code + " - " + m.Name);
        }

        private void AddTask()
        {
            Console.WriteLine("\n--- Add study task ---");
            if (planner.Modules.Count == 0) { InputHelper.Error("There are no modules yet. Add a module first (option 1)."); return; }

            string code = ChooseModuleCode("Module code for this task");
            string title = InputHelper.ReadRequiredText("Task title", Validation.MaxTitleLength);
            string desc = InputHelper.ReadOptionalText("Description", Validation.MaxDescriptionLength);
            DateTime due = InputHelper.ReadDate("Due date");
            double hours = InputHelper.ReadHours("Estimated study time");
            Priority pr = InputHelper.ReadPriority();
            StudyStatus st = InputHelper.ReadStatus(StudyStatus.NotStarted);   // Enter = Not Started

            StudyTask t = planner.AddTask(code, title, desc, due, hours, pr, st);
            InputHelper.Success("Task #" + t.Id + " added to " + t.ModuleCode + ".");
        }

        private void EditTask()
        {
            Console.WriteLine("\n--- Edit study task ---");
            StudyTask t = PickTask(planner.AllTasks(), "edit");
            if (t == null) return;

            Console.WriteLine("Press Enter to keep the current value shown in [brackets].");
            t.Title = InputHelper.ReadRequiredText("Task title", Validation.MaxTitleLength, t.Title);
            t.Description = InputHelper.ReadOptionalText("Description", Validation.MaxDescriptionLength, t.Description);
            t.DueDate = InputHelper.ReadDate("Due date", t.DueDate);
            t.EstimatedHours = InputHelper.ReadHours("Estimated study time", t.EstimatedHours);
            t.Priority = InputHelper.ReadPriority(t.Priority);
            t.Status = InputHelper.ReadStatus(t.Status);
            planner.IsDirty = true;
            InputHelper.Success("Task #" + t.Id + " updated.");
        }

        private void DeleteTask()
        {
            Console.WriteLine("\n--- Delete study task ---");
            StudyTask t = PickTask(planner.AllTasks(), "delete");
            if (t == null) return;
            if (!InputHelper.AskYesNo("Really delete task #" + t.Id + " '" + t.Title + "'?")) { InputHelper.Info("Delete cancelled."); return; }
            planner.DeleteTask(t.Id);
            InputHelper.Success("Task deleted.");
        }

        private void AssignTask()
        {
            Console.WriteLine("\n--- Assign task to a module ---");
            StudyTask t = PickTask(planner.AllTasks(), "assign");
            if (t == null) return;
            Console.WriteLine("Currently in module: " + t.ModuleCode);
            string code = ChooseModuleCode("Move to module code");
            if (string.Equals(code, t.ModuleCode, StringComparison.OrdinalIgnoreCase)) { InputHelper.Info("Task is already in that module."); return; }
            planner.MoveTask(t, code);
            InputHelper.Success("Task #" + t.Id + " is now assigned to " + t.ModuleCode + ".");
        }

        private void MarkCompleted()
        {
            Console.WriteLine("\n--- Mark task completed ---");
            StudyTask t = PickTask(planner.Outstanding(), "mark as completed");
            if (t == null) return;
            planner.MarkCompleted(t);
            InputHelper.Success("Task #" + t.Id + " '" + t.Title + "' marked as Completed.");
        }

        // ----- Viewing -----

        private void ViewAll()
        {
            Console.WriteLine("\n=== All modules and study tasks ===");
            if (planner.Modules.Count == 0) { InputHelper.Info("No modules yet."); return; }
            foreach (Module m in planner.Modules)
            {
                int done = m.Tasks.Count(t => !t.IsOutstanding);
                Console.WriteLine("\n[" + m.Code + "] " + m.Name + "  (" + m.Tasks.Count + " tasks, " + done + " completed)");
                if (m.Tasks.Count == 0) Console.WriteLine("   (no tasks)");
                else PrintTasks(m.Tasks, true);
            }
        }

        private void FilterTasks()
        {
            Console.WriteLine("\n--- Filter tasks ---");
            Console.WriteLine("1. By module");
            Console.WriteLine("2. By status");
            string c = InputHelper.ReadLine("Choose: ");
            if (c == "1")
            {
                if (planner.Modules.Count == 0) { InputHelper.Error("No modules to filter by."); return; }
                string code = ChooseModuleCode("Module code");
                var list = planner.FilterByModule(code).ToList();
                Console.WriteLine("\nTasks in module " + code + ":");
                PrintOrNone(list);
            }
            else if (c == "2")
            {
                StudyStatus st = InputHelper.ReadStatus();
                var list = planner.FilterByStatus(st).ToList();
                Console.WriteLine("\nTasks with status '" + Validation.StatusToText(st) + "':");
                PrintOrNone(list);
            }
            else InputHelper.Error("Please enter 1 or 2.");
        }

        private void SortTasks()
        {
            Console.WriteLine("\n--- Sort tasks ---");
            Console.WriteLine("1. Due date (earliest first)");
            Console.WriteLine("2. Due date (latest first)");
            Console.WriteLine("3. Priority (highest first)");
            Console.WriteLine("4. Priority (lowest first)");
            string c = InputHelper.ReadLine("Choose: ");
            IEnumerable<StudyTask> sorted;
            switch (c)
            {
                case "1": sorted = planner.AllTasks().OrderBy(t => t.DueDate).ThenByDescending(t => t.Priority); break;
                case "2": sorted = planner.AllTasks().OrderByDescending(t => t.DueDate).ThenByDescending(t => t.Priority); break;
                case "3": sorted = planner.AllTasks().OrderByDescending(t => t.Priority).ThenBy(t => t.DueDate); break;
                case "4": sorted = planner.AllTasks().OrderBy(t => t.Priority).ThenBy(t => t.DueDate); break;
                default: InputHelper.Error("Please enter a number from 1 to 4."); return;
            }
            Console.WriteLine();
            PrintOrNone(sorted.ToList());
        }

        private void ViewOutstandingAndCompleted()
        {
            Console.WriteLine("\n=== Outstanding tasks ===");
            PrintOrNone(planner.Outstanding().OrderBy(t => t.DueDate).ToList());
            Console.WriteLine("\n=== Completed tasks ===");
            PrintOrNone(planner.Completed().OrderBy(t => t.DueDate).ToList());
        }

        private void ViewDueSoonAndTime()
        {
            DateTime today = DateTime.Today;
            Console.WriteLine("\n=== Due soon (overdue or within " + StudyPlanner.DueSoonDays + " days) ===");
            PrintOrNone(planner.DueSoon(today).ToList());

            Console.WriteLine("\n=== Estimated study time still to do ===");
            foreach (Module m in planner.Modules)
            {
                double h = m.Tasks.Where(t => t.IsOutstanding).Sum(t => t.EstimatedHours);
                int n = m.Tasks.Count(t => t.IsOutstanding);
                Console.WriteLine("  " + Fit(m.Code, 12) + " " + n + " outstanding task(s), " + h.ToString("0.##", CultureInfo.InvariantCulture) + " hours");
            }
            Console.WriteLine("  ------------------------------------------");
            Console.WriteLine("  TOTAL outstanding study time: " + planner.TotalOutstandingHours().ToString("0.##", CultureInfo.InvariantCulture) + " hours");
        }

        private void ShowRecommendation()
        {
            Console.WriteLine("\n=== Smart study recommendation ===");
            List<Recommendation> ranked = recommender.Rank(planner.AllTasks(), DateTime.Today);
            if (ranked.Count == 0) { InputHelper.Info("No outstanding tasks - nothing to recommend. Well done!"); return; }

            Recommendation best = ranked[0];
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Recommended next study task: " + best.Task.Title + "  [" + best.Task.ModuleCode + ", #" + best.Task.Id + "]");
            Console.WriteLine("Reason: " + best.Reason);
            Console.ResetColor();
            Console.WriteLine("(Score " + best.Score + ", due " + best.Task.DueDate.ToString("yyyy-MM-dd") + ", " +
                              best.Task.EstimatedHours.ToString("0.##", CultureInfo.InvariantCulture) + "h)");

            if (ranked.Count > 1)
            {
                Console.WriteLine("\nOther tasks to consider:");
                foreach (Recommendation r in ranked.Skip(1).Take(3))
                    Console.WriteLine("  - " + r.Task.Title + " [" + r.Task.ModuleCode + "] score " + r.Score + ": " + r.Reason);
            }
        }

        // ----- File handling -----

        private void SaveToFile()
        {
            Console.WriteLine("\n--- Save data ---");
            string path = AskFilePath(currentFile);
            if (path == null) return;

            try
            {
                // Prompt before overwriting an existing file (requirement 5).
                if (File.Exists(path) && !InputHelper.AskYesNo("'" + Path.GetFileName(path) + "' already exists. Overwrite it?"))
                {
                    InputHelper.Info("Save cancelled.");
                    return;
                }
                DataStore.Save(planner, path);
                planner.IsDirty = false;
                currentFile = path;
                InputHelper.Success("Saved " + planner.AllTasks().Count() + " task(s) in " + planner.Modules.Count + " module(s) to " + path);
            }
            catch (UnauthorizedAccessException) { InputHelper.Error("Permission denied writing to '" + path + "'. Choose another location."); }
            catch (DirectoryNotFoundException) { InputHelper.Error("The folder for '" + path + "' does not exist."); }
            catch (PathTooLongException) { InputHelper.Error("The file path is too long."); }
            catch (IOException ex) { InputHelper.Error("Could not write the file: " + ex.Message); }
        }

        private void LoadMenu()
        {
            Console.WriteLine("\n--- Load data ---");
            if (planner.IsDirty && !InputHelper.AskYesNo("You have unsaved changes that will be replaced. Continue?")) return;
            string path = AskFilePath(currentFile);
            if (path != null) LoadFromFile(path);
        }

        /// <summary>Loads from a file, reporting missing/corrupted files and skipped lines without crashing.</summary>
        private void LoadFromFile(string path)
        {
            var warnings = new List<string>();
            try
            {
                List<Module> loaded = DataStore.Load(path, warnings);
                planner.ReplaceData(loaded);               // only applied when the whole load succeeded
                currentFile = path;
                InputHelper.Success("Loaded " + planner.AllTasks().Count() + " task(s) in " + planner.Modules.Count + " module(s) from " + path);
                if (warnings.Count > 0)
                {
                    InputHelper.Info(warnings.Count + " problem line(s) were ignored because the file is partly corrupted:");
                    foreach (string w in warnings.Take(10)) Console.WriteLine("    " + w);
                    if (warnings.Count > 10) Console.WriteLine("    ... and " + (warnings.Count - 10) + " more.");
                }
            }
            catch (FileNotFoundException) { InputHelper.Error("File not found: " + path + ". Nothing was loaded."); }
            catch (InvalidDataException ex) { InputHelper.Error("File is corrupted or not a planner file: " + ex.Message + " Existing data was kept."); }
            catch (UnauthorizedAccessException) { InputHelper.Error("Permission denied reading '" + path + "'."); }
            catch (IOException ex) { InputHelper.Error("Could not read the file: " + ex.Message); }
        }

        private void LoadSampleData()
        {
            Console.WriteLine("\n--- Load sample data ---");
            if (planner.Modules.Count > 0 &&
                !InputHelper.AskYesNo("This will replace the current data in memory. Continue?")) return;

            planner.ReplaceData(new List<Module>());
            DateTime d = DateTime.Today;                                   // dates relative to today so due-soon/overdue appear
            planner.AddModule("MOD002822", "Software Implementation");
            planner.AddModule("MOD002823", "Database Systems");
            planner.AddModule("MOD002824", "Web Technologies");
            planner.AddTask("MOD002822", "Programming Exercise 5", "Finish loops and arrays practical", d.AddDays(2), 2, Priority.High, StudyStatus.InProgress);
            planner.AddTask("MOD002822", "Smart Study Planner coursework", "Implement file I/O and recommender", d.AddDays(30), 20, Priority.High, StudyStatus.NotStarted);
            planner.AddTask("MOD002822", "Revise C# collections", "Lists, dictionaries, LINQ", d.AddDays(10), 3, Priority.Medium, StudyStatus.NotStarted);
            planner.AddTask("MOD002823", "SQL joins worksheet", "", d.AddDays(-1), 1.5, Priority.Medium, StudyStatus.NotStarted);
            planner.AddTask("MOD002823", "Normalisation exercises", "1NF to BCNF", d.AddDays(6), 4, Priority.Low, StudyStatus.NotStarted);
            planner.AddTask("MOD002824", "HTML/CSS portfolio page", "", d.AddDays(14), 6, Priority.Medium, StudyStatus.InProgress);
            planner.AddTask("MOD002824", "Read JavaScript chapter 3", "", d.AddDays(-5), 2, Priority.Low, StudyStatus.Completed);
            planner.IsDirty = true;
            InputHelper.Success("Sample data loaded (3 modules, 7 tasks).");
        }

        /// <summary>Asks for a file name; Enter uses the default. Relative names are placed next to the program.</summary>
        private string AskFilePath(string defaultPath)
        {
            string s = InputHelper.ReadLine("File name [" + Path.GetFileName(defaultPath) + "]: ");
            if (s.Length == 0) return defaultPath;
            if (s.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || s.IndexOfAny(new[] { '*', '?', '<', '>', '|' }) >= 0)
            {
                InputHelper.Error("That file name contains invalid characters.");
                return null;
            }
            if (!Path.HasExtension(s)) s += ".csv";
            return Path.IsPathRooted(s) ? s : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, s);
        }

        /// <summary>Returns true if the program should exit (after offering to save unsaved changes).</summary>
        private bool ConfirmExit()
        {
            if (planner.IsDirty && InputHelper.AskYesNo("You have unsaved changes. Save before exiting?"))
                SaveToFile();
            if (planner.IsDirty && !InputHelper.AskYesNo("Data is still unsaved. Exit anyway?"))
                return false;
            Console.WriteLine("Goodbye - good luck with your studies!");
            return true;
        }

        // ----- Shared UI helpers -----

        /// <summary>Shows the modules and asks for an existing module code, repeating until one is valid.</summary>
        private string ChooseModuleCode(string prompt)
        {
            Console.WriteLine("Available modules:");
            foreach (Module m in planner.Modules) Console.WriteLine("   " + m.Code + " - " + m.Name);
            while (true)
            {
                string code = InputHelper.ReadModuleCode(prompt);
                if (planner.FindModule(code) != null) return code;
                InputHelper.Error("No module with code '" + code + "'. Choose one from the list above.");
            }
        }

        /// <summary>Lists the given tasks and asks for a task ID. Returns null if cancelled or none available.</summary>
        private StudyTask PickTask(IEnumerable<StudyTask> candidates, string verb)
        {
            List<StudyTask> list = candidates.OrderBy(t => t.Id).ToList();
            if (list.Count == 0) { InputHelper.Error("There are no tasks available to " + verb + "."); return null; }
            PrintTasks(list, false);
            while (true)
            {
                string s = InputHelper.ReadLine("Enter the task ID to " + verb + " (Enter to cancel): ");
                if (s.Length == 0) { InputHelper.Info("Cancelled."); return null; }
                int id;
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                { InputHelper.Error("'" + s + "' is not a whole number."); continue; }
                StudyTask t = list.FirstOrDefault(x => x.Id == id);
                if (t == null) { InputHelper.Error("No selectable task has ID " + id + "."); continue; }
                return t;
            }
        }

        private void PrintOrNone(List<StudyTask> tasks)
        {
            if (tasks.Count == 0) Console.WriteLine("   (no tasks to show)");
            else PrintTasks(tasks, true);
        }

        /// <summary>Prints tasks as an aligned table with an OVERDUE / DUE SOON note for outstanding tasks.</summary>
        private void PrintTasks(IEnumerable<StudyTask> tasks, bool showDescription)
        {
            DateTime today = DateTime.Today;
            Console.WriteLine("   " + Fit("ID", 4) + Fit("Module", 12) + Fit("Title", 30) + Fit("Due", 12) +
                              Fit("Hours", 7) + Fit("Priority", 9) + Fit("Status", 12) + "Note");
            Console.WriteLine("   " + new string('-', 96));
            foreach (StudyTask t in tasks)
            {
                string note = "";
                if (t.IsOutstanding)
                {
                    int days = t.DaysUntilDue(today);
                    if (days < 0) note = "OVERDUE";
                    else if (days <= StudyPlanner.DueSoonDays) note = "DUE SOON";
                }
                Console.WriteLine("   " + Fit(t.Id.ToString(), 4) + Fit(t.ModuleCode, 12) + Fit(t.Title, 30) +
                                  Fit(t.DueDate.ToString("yyyy-MM-dd"), 12) +
                                  Fit(t.EstimatedHours.ToString("0.##", CultureInfo.InvariantCulture), 7) +
                                  Fit(t.Priority.ToString(), 9) + Fit(t.StatusText, 12) + note);
                if (showDescription && !string.IsNullOrEmpty(t.Description))
                    Console.WriteLine("        " + t.Description);
            }
        }

        /// <summary>Pads or truncates text to a fixed column width.</summary>
        private static string Fit(string s, int width)
        {
            if (s == null) s = "";
            return s.Length < width ? s.PadRight(width) : s.Substring(0, width - 3) + "...";
        }
    }

    // ---------------------------------------------------------------------------------
    //  ENTRY POINT
    // ---------------------------------------------------------------------------------

    public static class Program
    {
        public static void Main()
        {
            var planner = new StudyPlanner();
            IRecommender recommender = new ScoreBasedRecommender();
            new ConsoleUI(planner, recommender).Run();
        }
    }
}
