using System.ComponentModel;
using System.Runtime.CompilerServices;
using KcsPdfSigner.Core;

namespace KcsPdfSigner.App;

/// <summary>Grid-friendly wrapper so a detected spot can be reviewed and switched off.</summary>
public sealed class PlacementRow : INotifyPropertyChanged
{
    public PlacementRow(SignaturePlacement placement)
    {
        Placement = placement;
    }

    public SignaturePlacement Placement { get; }

    public bool Enabled
    {
        get => Placement.Enabled;
        set
        {
            if (Placement.Enabled == value) return;
            Placement.Enabled = value;
            OnPropertyChanged();
        }
    }

    private int _index;

    /// <summary>1-based number shown in the grid and on the page overlay.</summary>
    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            OnPropertyChanged();
        }
    }

    public int Page => Placement.PageNumber;

    public string Source => Placement.Source switch
    {
        PlacementSource.TextAnchor => "Anchor",
        PlacementSource.ExistingField => "Field",
        PlacementSource.FixedPosition => "Fixed",
        _ => "Manual"
    };

    public string Label => Placement.Label;

    public string Position =>
        $"{Placement.Left:0}, {Placement.Bottom:0}  ({Placement.Width:0}x{Placement.Height:0})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
