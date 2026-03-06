using CommunityToolkit.Mvvm.ComponentModel;
using CommandDashboard.Configuration;

namespace CommandDashboard.Models;

public partial class FieldUnitStatus : ObservableObject
{
    public FieldUnitStatus(string displayName, UnitRole role)
    {
        this.displayName = displayName;
        this.role = role;
        this.status = UnitStatus.Unknown;
        this.directionToEscort = "-";
    }

    [ObservableProperty]
    private string displayName;

    [ObservableProperty]
    private UnitRole role;

    [ObservableProperty]
    private UnitStatus status;

    [ObservableProperty]
    private double distanceMeters;

    [ObservableProperty]
    private string directionToEscort;

    [ObservableProperty]
    private double latitude;

    [ObservableProperty]
    private double longitude;

    [ObservableProperty]
    private string? operatorControl;

    public bool IsOutOfRange => Status == UnitStatus.Out;

    public string StatusDisplayText
    {
        get
        {
            var baseStatus = Status == UnitStatus.Danger ? "Warning" : Status.ToString();

            if (string.Equals(OperatorControl, "STOP", StringComparison.OrdinalIgnoreCase))
                return $"{baseStatus} (Hold)";

            if (string.Equals(OperatorControl, "HURRY", StringComparison.OrdinalIgnoreCase))
                return $"{baseStatus} (Hurry)";

            return baseStatus;
        }
    }

    partial void OnStatusChanged(UnitStatus value)
    {
        OnPropertyChanged(nameof(IsOutOfRange));
        OnPropertyChanged(nameof(StatusDisplayText));
    }

    partial void OnDistanceMetersChanged(double value)
    {
        OnPropertyChanged(nameof(IsOutOfRange));
        OnPropertyChanged(nameof(StatusDisplayText));
    }

    partial void OnOperatorControlChanged(string? value)
    {
        OnPropertyChanged(nameof(StatusDisplayText));
    }
}