using System.Collections.ObjectModel;
using System.Text;
using CommandMessaging;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.RealTime;
using FieldMobileApp.Location;
using FieldMobileApp.RealTime;

namespace FieldMobileApp;

public partial class MainPage
{
    private static readonly TimeSpan EscortVipActiveWindow = TimeSpan.FromSeconds(6);
    private static readonly Color StatusInColor = Color.FromArgb("#2E7D32");
    private static readonly Color StatusOutColor = Color.FromArgb("#C62828");
    private static readonly Color StatusWarningColor = Color.FromArgb("#F9A825");
    private static readonly Color StatusNeutralColor = Color.FromArgb("#455A64");
    private static readonly Color VipCommandStopBackgroundColor = Color.FromArgb("#CCA61B1B");
    private static readonly Color VipCommandStopStrokeColor = Color.FromArgb("#FFEF4444");
    private static readonly Color VipCommandHurryBackgroundColor = Color.FromArgb("#CC1D4ED8");
    private static readonly Color VipCommandHurryStrokeColor = Color.FromArgb("#FF93C5FD");
    private static readonly Color VipCommandOkBackgroundColor = Color.FromArgb("#CC065F46");
    private static readonly Color VipCommandOkStrokeColor = Color.FromArgb("#FF86EFAC");

    private readonly ObservableCollection<VipStatusListItem> escortVipStatuses = new();
    private readonly Dictionary<string, VipStatusListItem> escortVipListItemByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VipStatusEntry> escortVipStatusByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VipPositionEntry> escortVipPositionByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly EscortVipDynamicEntityDataSource escortVipDynamicEntityDataSource = new();
    private readonly HashSet<DynamicEntity> subscribedEscortVipDynamicEntities = new(ReferenceEqualityComparer.Instance);
    private readonly Lock escortVipDynamicEntityGate = new();
    private string displayedDistance = "  --.- m";
    private string displayedDirection = "-";
    private string? lastRenderedDetailText;
    private bool escortVipDynamicEntityInitialized;
    private bool vipDirectiveActive;
    private string? vipDirectiveSignal;

    private bool IsEscortRole() => string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase);

    private bool IsVipRole() => string.Equals(configuredRole, "VIP", StringComparison.OrdinalIgnoreCase);

    private void ApplyStatusVisual(string? status, string? detail)
    {
        var normalized = status?.Trim();
        var shouldShowVipRejoinBanner = IsVipRole() && string.Equals(normalized, "Out", StringComparison.OrdinalIgnoreCase);
        VipRejoinBanner.IsVisible = shouldShowVipRejoinBanner;

        if (string.Equals(normalized, "In", StringComparison.OrdinalIgnoreCase))
        {
            BackgroundColor = StatusInColor;
            RoleStateLabel.Text = IsVipRole()
                ? "You are inside the security perimeter"
                : "IN";
        }
        else if (string.Equals(normalized, "Out", StringComparison.OrdinalIgnoreCase))
        {
            BackgroundColor = StatusOutColor;
            RoleStateLabel.Text = IsVipRole()
                ? "You are outside the security perimeter"
                : "OUT";
        }
        else if (string.Equals(normalized, "Warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "WARNING", StringComparison.OrdinalIgnoreCase))
        {
            BackgroundColor = StatusWarningColor;
            RoleStateLabel.Text = IsVipRole()
                ? "You are near the edge of the security perimeter"
                : "WARNING";
        }
        else
        {
            BackgroundColor = StatusNeutralColor;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                RoleStateLabel.Text = IsEscortRole()
                    ? "ACTIVE"
                    : !string.IsNullOrWhiteSpace(detail)
                        ? detail
                        : "CONNECTED";
            }
            else
            {
                RoleStateLabel.Text = IsVipRole()
                    ? $"Current status: {normalized}"
                    : normalized.ToUpperInvariant();
            }
        }

        if (!string.IsNullOrWhiteSpace(detail)
            && !IsVipRole())
        {
            SetDetailTextIfChanged(detail);
        }
    }

    private void UpdateEscortMetricsDetail()
    {
        var directionToken = string.IsNullOrWhiteSpace(displayedDirection) || string.Equals(displayedDirection, "-", StringComparison.Ordinal)
            ? "--"
            : displayedDirection.Trim().ToUpperInvariant();

        if (directionToken.Length > 2)
            directionToken = directionToken[..2];
        else if (directionToken.Length < 2)
            directionToken = directionToken.PadLeft(2, ' ');

        SetDetailTextIfChanged($"Escort: {displayedDistance} {directionToken}");
    }

    private async Task EnsureEscortVipDynamicEntityDataSourceAsync(string role)
    {
        if (!string.Equals(role, "Escort", StringComparison.OrdinalIgnoreCase)
            || escortVipDynamicEntityInitialized)
            return;

        await escortVipDynamicEntityDataSource.LoadAsync();
        await escortVipDynamicEntityDataSource.ConnectAsync();
        escortVipDynamicEntityDataSource.DynamicEntityReceived += OnEscortVipDynamicEntityReceived;
        escortVipDynamicEntityDataSource.DynamicEntityPurged += OnEscortVipDynamicEntityPurged;
        escortVipDynamicEntityInitialized = true;
    }

    private void OnEscortVipDynamicEntityReceived(object? sender, DynamicEntityEventArgs eventArgs)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            foreach (var dynamicEntity in EnumerateDynamicEntities(eventArgs))
            {
                TrySubscribeEscortVipDynamicEntityChanged(dynamicEntity);
                UpsertEscortVipStatusFromDynamicEntity(dynamicEntity);
            }
        });
    }

    private void OnEscortVipDynamicEntityPurged(object? sender, DynamicEntityEventArgs eventArgs)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var changed = false;
            foreach (var dynamicEntity in EnumerateDynamicEntities(eventArgs))
            {
                var entityKey = GetDynamicEntityUniqueKey(dynamicEntity);
                if (entityKey is null)
                    continue;

                escortVipStatusByDevice.Remove(entityKey);
                escortVipPositionByDevice.Remove(entityKey);
                changed = true;
            }

            if (changed)
            {
                RebuildEscortVipList();
            }
        });
    }

    private void TrySubscribeEscortVipDynamicEntityChanged(DynamicEntity dynamicEntity)
    {
        lock (escortVipDynamicEntityGate)
        {
            if (!subscribedEscortVipDynamicEntities.Add(dynamicEntity))
                return;

            dynamicEntity.DynamicEntityChanged += OnEscortVipDynamicEntityChanged;
        }
    }

    private void OnEscortVipDynamicEntityChanged(object? sender, DynamicEntityChangedEventArgs eventArgs)
    {
        if (sender is not DynamicEntity dynamicEntity)
            return;

        MainThread.BeginInvokeOnMainThread(() => UpsertEscortVipStatusFromDynamicEntity(dynamicEntity));
    }

    private void UpsertEscortVipStatusFromDynamicEntity(DynamicEntity dynamicEntity)
    {
        var entityKey = GetDynamicEntityUniqueKey(dynamicEntity);
        if (entityKey is null)
            return;

        var displayName = NormalizeVipDeviceId(ReadDynamicEntityString(dynamicEntity.Attributes, EscortVipDynamicEntityDataSource.EntityIdFieldName))
            ?? entityKey;
        var status = ReadDynamicEntityString(dynamicEntity.Attributes, EscortVipDynamicEntityDataSource.StatusFieldName);
        UpsertEscortVipStatus(entityKey, displayName, status);

        var latitude = ReadDynamicEntityDouble(dynamicEntity.Attributes, "latitude");
        var longitude = ReadDynamicEntityDouble(dynamicEntity.Attributes, "longitude");
        if (latitude.HasValue && longitude.HasValue)
        {
            UpdateEscortVipPosition(entityKey, latitude.Value, longitude.Value);
        }
    }

    private static IEnumerable<DynamicEntity> EnumerateDynamicEntities(DynamicEntityEventArgs eventArgs)
    {
        if (eventArgs.DynamicEntity is not null)
        {
            yield return eventArgs.DynamicEntity;
        }
    }

    private static string? ReadDynamicEntityString(IDictionary<string, object?> attributes, string key)
    {
        return GetDynamicEntityAttributeValue(attributes, key)?.ToString();
    }

    private static double? ReadDynamicEntityDouble(IDictionary<string, object?> attributes, string key)
    {
        var value = GetDynamicEntityAttributeValue(attributes, key);
        if (value is null)
            return null;

        if (value is double doubleValue)
            return doubleValue;

        if (double.TryParse(value.ToString(), out var parsedValue))
            return parsedValue;

        return null;
    }

    private static object? GetDynamicEntityAttributeValue(IDictionary<string, object?> attributes, string key)
    {
        foreach (var pair in attributes)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private void UpsertEscortVipStatus(string entityKey, string displayName, string? status)
    {
        var normalizedEntityKey = NormalizeVipDeviceId(entityKey);
        var normalizedDisplayName = NormalizeVipDeviceId(displayName);
        if (!IsEscortRole()
            || normalizedEntityKey is null
            || normalizedDisplayName is null)
        {
            return;
        }

        var normalizedStatus = NormalizeVipStatusLabel(status);
        escortVipStatusByDevice[normalizedEntityKey] = new VipStatusEntry(
            normalizedDisplayName,
            normalizedStatus,
            ResolveVipStatusBackground(normalizedStatus),
            DateTimeOffset.UtcNow);

        RebuildEscortVipList();
    }

    private void RebuildEscortVipList()
    {
        var activeCutoff = DateTimeOffset.UtcNow - EscortVipActiveWindow;

        var activeDeviceIds = escortVipStatusByDevice
            .Where(pair => pair.Value.LastSeenUtc >= activeCutoff)
            .Select(pair => pair.Key)
            .OrderBy(deviceId => deviceId, Comparer<string>.Create(CompareVipDeviceIds))
            .ToList();
        var activeDeviceIdSet = new HashSet<string>(activeDeviceIds, StringComparer.OrdinalIgnoreCase);

        var staleDeviceIds = escortVipListItemByDevice.Keys
            .Where(deviceId => !activeDeviceIdSet.Contains(deviceId))
            .ToList();

        foreach (var staleDeviceId in staleDeviceIds)
        {
            if (!escortVipListItemByDevice.TryGetValue(staleDeviceId, out var staleItem))
                continue;

            escortVipStatuses.Remove(staleItem);
            escortVipListItemByDevice.Remove(staleDeviceId);
        }

        for (var targetIndex = 0; targetIndex < activeDeviceIds.Count; targetIndex++)
        {
            var entityKey = activeDeviceIds[targetIndex];
            if (!escortVipStatusByDevice.TryGetValue(entityKey, out var entry))
                continue;

            if (!escortVipListItemByDevice.TryGetValue(entityKey, out var item))
            {
                item = new VipStatusListItem(entry.DisplayName, entry.Status, entry.BackgroundColor);
                escortVipListItemByDevice[entityKey] = item;
                escortVipStatuses.Insert(Math.Min(targetIndex, escortVipStatuses.Count), item);
            }
            else
            {
                var existingIndex = escortVipStatuses.IndexOf(item);
                if (existingIndex >= 0 && existingIndex != targetIndex)
                {
                    escortVipStatuses.Move(existingIndex, targetIndex);
                }

                item.DeviceId = entry.DisplayName;
                item.Status = entry.Status;
                item.BackgroundColor = entry.BackgroundColor;
            }
        }

        UpdateEscortOverallStatusBanner();
    }

    private void UpdateEscortOverallStatusBanner()
    {
        if (!IsEscortRole())
            return;

        var activeCutoff = DateTimeOffset.UtcNow - EscortVipActiveWindow;
        var activeStatuses = escortVipStatusByDevice
            .Where(pair => pair.Value.LastSeenUtc >= activeCutoff)
            .Select(pair => pair.Value.Status)
            .ToList();

        static string BuildCountSummary(int affectedCount, int totalCount, string stateLabel)
        {
            var verb = affectedCount == 1 ? "is" : "are";
            return $"{affectedCount} of {totalCount} VIPs {verb} {stateLabel}";
        }

        if (activeStatuses.Count == 0)
        {
            EscortOverallStatusBanner.BackgroundColor = StatusNeutralColor;
            EscortOverallStatusLabel.Text = "Awaiting VIP telemetry";
            return;
        }

        var outsideCount = activeStatuses.Count(status => string.Equals(status, "Out", StringComparison.OrdinalIgnoreCase));
        if (outsideCount > 0)
        {
            EscortOverallStatusBanner.BackgroundColor = StatusOutColor;
            EscortOverallStatusLabel.Text = BuildCountSummary(outsideCount, activeStatuses.Count, "outside the perimeter");
            return;
        }

        var warningCount = activeStatuses.Count(status => string.Equals(status, "WARNING", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Warning", StringComparison.OrdinalIgnoreCase));
        if (warningCount > 0)
        {
            EscortOverallStatusBanner.BackgroundColor = StatusWarningColor;
            EscortOverallStatusLabel.Text = BuildCountSummary(warningCount, activeStatuses.Count, "in the warning perimeter");
            return;
        }

        EscortOverallStatusBanner.BackgroundColor = StatusInColor;
        EscortOverallStatusLabel.Text = BuildCountSummary(activeStatuses.Count, activeStatuses.Count, "inside the perimeter");
    }

    private void UpdateEscortVipPosition(string entityKey, double latitude, double longitude)
    {
        var normalizedEntityKey = NormalizeVipDeviceId(entityKey);
        if (!IsEscortRole()
            || normalizedEntityKey is null)
        {
            return;
        }

        escortVipPositionByDevice[normalizedEntityKey] = new VipPositionEntry(latitude, longitude, DateTimeOffset.UtcNow);
    }

    private static int CompareVipDeviceIds(string? left, string? right)
    {
        left ??= string.Empty;
        right ??= string.Empty;

        var leftHasNumericSuffix = TryGetTrailingNumber(left, out var leftNumber);
        var rightHasNumericSuffix = TryGetTrailingNumber(right, out var rightNumber);

        if (leftHasNumericSuffix && rightHasNumericSuffix)
        {
            var textCompare = string.Compare(GetPrefixWithoutTrailingNumber(left), GetPrefixWithoutTrailingNumber(right), StringComparison.OrdinalIgnoreCase);
            if (textCompare != 0)
                return textCompare;

            return leftNumber.CompareTo(rightNumber);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetTrailingNumber(string value, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var end = value.Length - 1;
        while (end >= 0 && char.IsDigit(value[end]))
        {
            end--;
        }

        if (end == value.Length - 1)
            return false;

        var numericPart = value[(end + 1)..];
        return int.TryParse(numericPart, out number);
    }

    private static string GetPrefixWithoutTrailingNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var end = value.Length - 1;
        while (end >= 0 && char.IsDigit(value[end]))
        {
            end--;
        }

        return end >= 0 ? value[..(end + 1)].TrimEnd('-', ' ') : string.Empty;
    }

    private static string? NormalizeVipDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        var normalizedForm = deviceId.Normalize(NormalizationForm.FormKC).Trim();
        if (normalizedForm.Length == 0)
            return null;

        var builder = new StringBuilder(normalizedForm.Length);
        var previousWasWhitespace = false;

        foreach (var character in normalizedForm)
        {
            if (character is '‐' or '‑' or '‒' or '–' or '—' or '―' or '−')
            {
                builder.Append('-');
                previousWasWhitespace = false;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (previousWasWhitespace)
                    continue;

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        var canonical = builder.ToString().Trim();
        return canonical.Length == 0 ? null : canonical;
    }

    private static string? GetDynamicEntityUniqueKey(DynamicEntity dynamicEntity)
    {
        var dynamicEntityIdProperty = dynamicEntity.GetType().GetProperty("DynamicEntityId");
        if (dynamicEntityIdProperty?.GetValue(dynamicEntity) is object dynamicEntityId)
        {
            var normalized = NormalizeVipDeviceId(dynamicEntityId.ToString());
            if (normalized is not null)
                return normalized;
        }

        var fallbackTrackId = ReadDynamicEntityString(dynamicEntity.Attributes, EscortVipDynamicEntityDataSource.EntityIdFieldName);
        return NormalizeVipDeviceId(fallbackTrackId);
    }

    private static Color ResolveVipStatusBackground(string status)
    {
        if (string.Equals(status, "In", StringComparison.OrdinalIgnoreCase))
            return StatusInColor;

        if (string.Equals(status, "Warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "WARNING", StringComparison.OrdinalIgnoreCase))
            return StatusWarningColor;

        if (string.Equals(status, "Out", StringComparison.OrdinalIgnoreCase))
            return StatusOutColor;

        return StatusNeutralColor;
    }

    private static string NormalizeVipStatusLabel(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "Unknown";

        var normalizedStatus = status.Trim();
        if (string.Equals(normalizedStatus, "Warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedStatus, "WARNING", StringComparison.OrdinalIgnoreCase))
        {
            return "WARNING";
        }

        return normalizedStatus;
    }

    private void SetDetailTextIfChanged(string detailText)
    {
        if (string.Equals(lastRenderedDetailText, detailText, StringComparison.Ordinal))
            return;

        lastRenderedDetailText = detailText;
        DetailLabel.Text = detailText;
    }

    private void UpdateVipCommandBanner(string? signal, string? message, bool isActive)
    {
        if (!IsVipRole())
        {
            VipCommandBanner.IsVisible = false;
            vipDirectiveActive = false;
            vipDirectiveSignal = null;
            return;
        }

        if (!isActive)
        {
            VipCommandBanner.IsVisible = false;
            VipCommandLabel.Text = string.Empty;
            vipDirectiveActive = false;
            vipDirectiveSignal = null;
            return;
        }

        var normalizedSignal = signal?.Trim();
        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? "Operator command received"
            : message.Trim();

        if (string.Equals(normalizedSignal, "STOP", StringComparison.OrdinalIgnoreCase))
        {
            VipCommandBanner.Background = VipCommandStopBackgroundColor;
            VipCommandBanner.Stroke = VipCommandStopStrokeColor;
            VipCommandLabel.Text = $"🛑 {normalizedMessage}";
        }
        else if (string.Equals(normalizedSignal, "HURRY", StringComparison.OrdinalIgnoreCase))
        {
            VipCommandBanner.Background = VipCommandHurryBackgroundColor;
            VipCommandBanner.Stroke = VipCommandHurryStrokeColor;
            VipCommandLabel.Text = $"⚡ {normalizedMessage}";
        }
        else
        {
            VipCommandBanner.Background = VipCommandOkBackgroundColor;
            VipCommandBanner.Stroke = VipCommandOkStrokeColor;
            VipCommandLabel.Text = $"✅ {normalizedMessage}";
        }

        vipDirectiveActive = true;
        vipDirectiveSignal = normalizedSignal;
        VipCommandBanner.IsVisible = true;
    }

    private sealed class VipStatusListItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string deviceId;
        private string status;
        private Color backgroundColor;

        public VipStatusListItem(string deviceId, string status, Color backgroundColor)
        {
            this.deviceId = deviceId;
            this.status = status;
            this.backgroundColor = backgroundColor;
        }

        public string DeviceId
        {
            get => deviceId;
            set
            {
                if (string.Equals(deviceId, value, StringComparison.Ordinal))
                    return;

                deviceId = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DeviceId)));
            }
        }

        public string Status
        {
            get => status;
            set
            {
                if (string.Equals(status, value, StringComparison.Ordinal))
                    return;

                status = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public Color BackgroundColor
        {
            get => backgroundColor;
            set
            {
                if (backgroundColor == value)
                    return;

                backgroundColor = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BackgroundColor)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record VipStatusEntry(string DisplayName, string Status, Color BackgroundColor, DateTimeOffset LastSeenUtc);

    private sealed record VipPositionEntry(double Latitude, double Longitude, DateTimeOffset LastSeenUtc);
}
