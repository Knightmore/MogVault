using Dalamud.Bindings.ImGui;

namespace MogVault.Ui;

internal static class TableSort
{
    private static readonly Comparer<IComparable?> Order = Comparer<IComparable?>.Create(Compare);

    public static bool Apply<T>(ref T[] rows, ref bool dirty, Func<T, int, IComparable?> key) =>
        Apply(ref rows, ref dirty, _ => false, key);

    public static bool Apply<T>(ref T[] rows, ref bool dirty, Func<T, bool> pinned, Func<T, int, IComparable?> key)
    {
        var specs = ImGui.TableGetSortSpecs();
        if (!specs.SpecsDirty && !dirty)
            return false;
        specs.SpecsDirty = false;
        dirty = false;
        if (specs.SpecsCount == 0)
        {
            rows = [.. rows.OrderBy(r => pinned(r) ? 0 : 1).ThenBy(r => key(r, 0), Order)];
            return true;
        }

        var column = specs.Specs.ColumnIndex;
        rows = specs.Specs.SortDirection == ImGuiSortDirection.Descending
            ? [.. rows.OrderByDescending(r => key(r, column), Order)]
            : [.. rows.OrderBy(r => key(r, column), Order)];
        return true;
    }

    private static int Compare(IComparable? a, IComparable? b) =>
        a is string x && b is string y
            ? StringComparer.CurrentCultureIgnoreCase.Compare(x, y)
            : Comparer<IComparable?>.Default.Compare(a, b);
}

