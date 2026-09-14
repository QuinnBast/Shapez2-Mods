using System;
using System.Collections.Generic;
using System.Text;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.PlatformBlackbox;

/// <summary>
/// Prints the toolbar tree, with the index path of every element.
///
/// Toolbar positions are given as a path of indices - <c>Root().ChildAt(5).ChildAt(4)</c> -
/// and there is no way to locate an entry by id, so a mod adding a toolbar entry has to know
/// the real shape of the tree. Guessing is worse than it sounds: a path that resolves to the
/// wrong place inserts the entry successfully and silently, and looks identical to a
/// registration that failed.
///
/// So this captures the tree as Shifter hands it over and writes the paths out. Whatever
/// path the blackbox platform ought to use, this is where the answer comes from.
/// </summary>
public class ToolbarMap : IToolbarDataRewirer
{
    private readonly ILogger Logger;

    /// The last tree seen. Kept because the rewire happens long before anyone asks about it.
    private string Captured;

    public ToolbarMap(ILogger logger)
    {
        Logger = logger;
    }

    public ToolbarData ModifyToolbarData(ToolbarData toolbarData)
    {
        try
        {
            Captured = Describe(toolbarData);
        }
        catch (Exception exception)
        {
            // Never break the toolbar over a diagnostic.
            Logger.Exception?.LogException(exception);
        }

        return toolbarData;
    }

    public bool TryGet(out string tree)
    {
        tree = Captured;
        return tree != null;
    }

    private static string Describe(ToolbarData toolbarData)
    {
        StringBuilder text = new StringBuilder();
        text.Append("toolbar tree - paths are Root().ChildAt(..)...");

        object root = toolbarData?.RootToolbarElement;
        if (root == null)
        {
            return text.Append("\n  (no root)").ToString();
        }

        Walk(text, root, new List<int>(), 0);
        return text.ToString();
    }

    /// <summary>
    /// Walks by reflection rather than against the element types directly. The project leaves
    /// <c>RootToolbarElementData</c> and <c>ParentToolbarElementData</c> unpublicized, so
    /// their members are not all reachable by name, and the shape of the tree is not worth a
    /// compile-time dependency for a diagnostic.
    /// </summary>
    private static void Walk(StringBuilder text, object element, List<int> path, int depth)
    {
        if (element == null || depth > 6)
        {
            return;
        }

        text.Append('\n').Append(new string(' ', 2 + depth * 2));

        if (path.Count > 0)
        {
            text.Append("Root()");
            foreach (int index in path)
            {
                text.Append(".ChildAt(").Append(index).Append(')');
            }

            text.Append("  ");
        }
        else
        {
            text.Append("Root()  ");
        }

        text.Append(Label(element));

        IEnumerable<object> children = Children(element);
        if (children == null)
        {
            return;
        }

        int position = 0;
        foreach (object child in children)
        {
            path.Add(position);
            Walk(text, child, path, depth + 1);
            path.RemoveAt(path.Count - 1);
            position++;
        }
    }

    private static string Label(object element)
    {
        string name = element.GetType().Name;

        // A title is an IText, which resolves to the player's language; the raw key is more
        // useful here because it is what a mod would search for.
        object title = Read(element, "Title") ?? Read(element, "TitleText");
        return title == null ? name : name + "  \"" + title + "\"";
    }

    private static IEnumerable<object> Children(object element)
    {
        object children = Read(element, "Children");
        if (children is IEnumerable<object> typed)
        {
            return typed;
        }

        if (children is System.Collections.IEnumerable loose)
        {
            List<object> collected = new List<object>();
            foreach (object item in loose)
            {
                collected.Add(item);
            }

            return collected;
        }

        return null;
    }

    private static object Read(object target, string member)
    {
        Type type = target.GetType();

        System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        try
        {
            System.Reflection.PropertyInfo property = type.GetProperty(member, flags);
            if (property != null)
            {
                return property.GetValue(target);
            }

            System.Reflection.FieldInfo field = type.GetField(member, flags);
            return field?.GetValue(target);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
