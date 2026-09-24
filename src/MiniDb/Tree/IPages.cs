namespace MiniDb.Tree;

/// <summary>
/// The pages of one transaction, as the tree needs them. Reading gives the page as this
/// transaction sees it; modifying gives the transaction's own copy, which nothing else sees
/// until it commits.
/// </summary>
internal interface IPages
{
    /// <summary>The page to read. Its bytes must not be changed through this array.</summary>
    byte[] Read(uint page);

    /// <summary>The page to change: this transaction's own copy, the same array every time.</summary>
    byte[] Modify(uint page);

    /// <summary>A page for the tree to use, from the free list or past the end of the file.</summary>
    uint Allocate();

    /// <summary>Hand a page the tree no longer uses back to the free list.</summary>
    void Free(uint page);

    /// <summary>The root of the tree, kept in the header page.</summary>
    uint Root { get; set; }
}
