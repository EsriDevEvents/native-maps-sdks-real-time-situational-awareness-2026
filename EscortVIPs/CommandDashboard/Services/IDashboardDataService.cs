using CommandDashboard.Models;

namespace CommandDashboard.Services;

public interface IDashboardDataService
{
    IReadOnlyList<FieldUnitStatus> CreateInitialVipUnits();

    FieldUnitStatus CreateEscortUnit();

    void ApplyNextUpdate(FieldUnitStatus escortUnit, IList<FieldUnitStatus> vipUnits, bool isSimulationMode);
}