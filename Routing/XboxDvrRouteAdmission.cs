using System.Globalization;

namespace ClipsToDiscord;

/// <summary>
/// Pure, content-free preflight for Xbox DVR metadata. A returned route is only a potential
/// match: duration is deliberately unknown until the source is hydrated and the exact routing
/// evaluator remains the final authority.
/// </summary>
internal static class XboxDvrRouteAdmission
{
    internal static IReadOnlyList<RoutingRoute> FindPotentialRoutes(
        RoutingSnapshotDocument snapshot,
        string sourceConnectionId,
        string game,
        DateTimeOffset capturedUtc,
        string occurrenceId,
        string revisionId)
    {
        RoutingSnapshotModel.Validate(snapshot);
        RoutingInputSourceCatalogModel.ValidateSourceId(sourceConnectionId);
        RoutingValidation.RequireText(game, 1, 256, "Xbox game name");
        RoutingValidation.RequireUtc(capturedUtc, "Xbox capture timestamp");
        RoutingValidation.RequireSha256(occurrenceId, "Xbox occurrence identity");
        RoutingValidation.RequireSha256(revisionId, "Xbox revision identity");

        return snapshot.Routes
            .Select((route, listIndex) => new IndexedRoute(route, listIndex))
            .Where(item => item.Route.Enabled &&
                           item.Route.Kind == RoutingRouteKind.Specific &&
                           item.Route.Trigger == RoutingTriggerKind.WatchedFolder)
            .Where(item => HasExactSourceBinding(item.Route, sourceConnectionId))
            .Where(item => MatchesFrozenHistory(
                item.Route, capturedUtc, occurrenceId, revisionId))
            .Where(item => item.Route.Conditions.All(condition =>
                KnownMetadataCouldMatch(
                    condition,
                    sourceConnectionId,
                    game,
                    capturedUtc)))
            .OrderBy(item => item.Route.Priority)
            .ThenBy(item => item.ListIndex)
            .Select(item => item.Route)
            .ToArray();
    }

    private static bool MatchesFrozenHistory(
        RoutingRoute route,
        DateTimeOffset capturedUtc,
        string occurrenceId,
        string revisionId)
    {
        var selection = route.XboxHistorySelection ??
                        throw new InvalidDataException(
                            "An Xbox route has no frozen history selection.");
        return capturedUtc > selection.ActivationUtc ||
               selection.HistoricalOccurrences.Any(item =>
                   item.OccurrenceId.Equals(occurrenceId, StringComparison.Ordinal) &&
                   item.RevisionId.Equals(revisionId, StringComparison.Ordinal));
    }

    private static bool HasExactSourceBinding(
        RoutingRoute route,
        string sourceConnectionId) => route.Conditions.Any(condition =>
        condition.Field == RoutingConditionField.SourceConnection &&
        condition.Operator == RoutingConditionOperator.Equals &&
        condition.Value.Equals(sourceConnectionId, StringComparison.Ordinal));

    private static bool KnownMetadataCouldMatch(
        RoutingCondition condition,
        string sourceConnectionId,
        string game,
        DateTimeOffset capturedUtc) => condition.Field switch
        {
            RoutingConditionField.Game => CompareText(
                game, condition.Operator, condition.Value),
            RoutingConditionField.ClipSource => CompareEnum(
                RoutingClipSource.XboxOneDrive, condition.Operator, condition.Value),
            RoutingConditionField.ReactionCamera => CompareBoolean(
                actual: false, condition.Operator, condition.Value),
            RoutingConditionField.CaptureType => CompareMissingCaptureType(
                condition.Operator),
            // Duration is not known without opening the file. Retain both possible outcomes;
            // RoutingEvaluator will decide after hydration.
            RoutingConditionField.Duration => true,
            RoutingConditionField.SourceConnection => CompareSourceConnection(
                sourceConnectionId, condition.Operator, condition.Value),
            RoutingConditionField.CapturedAt => CompareCapturedAt(
                capturedUtc, condition.Operator, condition.Value),
            _ => throw new InvalidDataException("A route condition kind is unsupported.")
        };

    private static bool CompareText(
        string actual,
        RoutingConditionOperator comparison,
        string expected) => XboxDvrGameMatch.Matches(actual, comparison, expected);

    private static bool CompareEnum<TEnum>(
        TEnum actual,
        RoutingConditionOperator comparison,
        string expected)
        where TEnum : struct, Enum
    {
        var parsed = Enum.Parse<TEnum>(expected, ignoreCase: true);
        return comparison switch
        {
            RoutingConditionOperator.Equals =>
                EqualityComparer<TEnum>.Default.Equals(actual, parsed),
            RoutingConditionOperator.DoesNotEqual =>
                !EqualityComparer<TEnum>.Default.Equals(actual, parsed),
            _ => throw new InvalidDataException("An enum condition uses an unsupported operator.")
        };
    }

    private static bool CompareBoolean(
        bool actual,
        RoutingConditionOperator comparison,
        string expected)
    {
        var parsed = bool.Parse(expected);
        return comparison switch
        {
            RoutingConditionOperator.Equals => actual == parsed,
            RoutingConditionOperator.DoesNotEqual => actual != parsed,
            _ => throw new InvalidDataException("A boolean condition uses an unsupported operator.")
        };
    }

    private static bool CompareMissingCaptureType(
        RoutingConditionOperator comparison) => comparison switch
        {
            RoutingConditionOperator.Equals => false,
            RoutingConditionOperator.DoesNotEqual => true,
            _ => throw new InvalidDataException("An enum condition uses an unsupported operator.")
        };

    private static bool CompareSourceConnection(
        string actual,
        RoutingConditionOperator comparison,
        string expected) => comparison switch
        {
            RoutingConditionOperator.Equals =>
                actual.Equals(expected, StringComparison.Ordinal),
            RoutingConditionOperator.DoesNotEqual =>
                !actual.Equals(expected, StringComparison.Ordinal),
            _ => throw new InvalidDataException(
                "A source-connection condition uses an unsupported operator.")
        };

    private static bool CompareCapturedAt(
        DateTimeOffset actual,
        RoutingConditionOperator comparison,
        string expected)
    {
        var parsed = DateTimeOffset.ParseExact(
            expected,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
        return comparison switch
        {
            RoutingConditionOperator.GreaterThanOrEqual => actual >= parsed,
            RoutingConditionOperator.LessThanOrEqual => actual <= parsed,
            _ => throw new InvalidDataException(
                "A captured-at condition uses an unsupported operator.")
        };
    }

    private sealed record IndexedRoute(RoutingRoute Route, int ListIndex);
}
