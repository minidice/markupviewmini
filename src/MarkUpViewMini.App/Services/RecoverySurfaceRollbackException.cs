namespace MarkUpViewMini.App.Services;

internal sealed class RecoverySurfaceRollbackException(
    Exception activationFailure,
    Exception rollbackFailure) : Exception(
        "The recovery surface could not be restored.",
        new AggregateException(activationFailure, rollbackFailure))
{
}
