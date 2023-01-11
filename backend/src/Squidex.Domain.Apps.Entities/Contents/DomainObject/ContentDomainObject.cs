﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschraenkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using Microsoft.Extensions.Logging;
using NodaTime;
using Squidex.Domain.Apps.Core.Contents;
using Squidex.Domain.Apps.Core.Schemas;
using Squidex.Domain.Apps.Entities.Contents.Commands;
using Squidex.Domain.Apps.Entities.Contents.DomainObject.Guards;
using Squidex.Domain.Apps.Events;
using Squidex.Domain.Apps.Events.Contents;
using Squidex.Infrastructure;
using Squidex.Infrastructure.Commands;
using Squidex.Infrastructure.EventSourcing;
using Squidex.Infrastructure.Reflection;
using Squidex.Infrastructure.States;
using Squidex.Shared;

#pragma warning disable MA0022 // Return Task.FromResult instead of returning null

namespace Squidex.Domain.Apps.Entities.Contents.DomainObject;

public partial class ContentDomainObject : DomainObject<ContentDomainObject.State>
{
    private readonly IServiceProvider serviceProvider;

    public ContentDomainObject(DomainId id, IPersistenceFactory<State> persistence, ILogger<ContentDomainObject> log,
        IServiceProvider serviceProvider)
        : base(id, persistence, log)
    {
        this.serviceProvider = serviceProvider;
    }

    protected override bool IsDeleted(State snapshot)
    {
        return snapshot.IsDeleted;
    }

    protected override bool CanAcceptCreation(ICommand command)
    {
        return command is ContentCommandBase;
    }

    protected override bool CanRecreate()
    {
        return true;
    }

    protected override bool CanRecreate(IEvent @event)
    {
        return @event is ContentCreated;
    }

    protected override bool CanAccept(ICommand command)
    {
        return command is ContentCommand contentCommand &&
            Equals(contentCommand.AppId, Snapshot.AppId) &&
            Equals(contentCommand.SchemaId, Snapshot.SchemaId) &&
            Equals(contentCommand.ContentId, Snapshot.Id);
    }

    public override Task<CommandResult> ExecuteAsync(IAggregateCommand command,
        CancellationToken ct)
    {
        switch (command)
        {
            case UpsertContent upsertContent:
                return UpsertReturnAsync(upsertContent, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    if (Version <= EtagVersion.Empty || IsDeleted(Snapshot))
                    {
                        await CreateCore(c.AsCreate(), operation, ct);
                    }
                    else if (c.Patch)
                    {
                        await PatchCore(c.AsUpdate(), operation, ct);
                    }
                    else
                    {
                        await UpdateCore(c.AsUpdate(), operation, ct);
                    }

                    if (Is.OptionalChange(operation.Snapshot.EditingStatus(), c.Status))
                    {
                        await ChangeCore(c.AsChange(c.Status.Value), operation, ct);
                    }

                    return Snapshot;
                }, ct);

            case CreateContent createContent:
                return CreateReturnAsync(createContent, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    await CreateCore(c, operation, ct);

                    if (operation.Schema.SchemaDef.Type == SchemaType.Singleton)
                    {
                        ChangeStatus(c.AsChange(Status.Published));
                    }
                    else if (Is.OptionalChange(Snapshot.Status, c.Status))
                    {
                        await ChangeCore(c.AsChange(c.Status.Value), operation, ct);
                    }

                    return Snapshot;
                }, ct);

            case ValidateContent validate:
                return UpdateReturnAsync(validate, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    await ValidateCore(operation, ct);

                    return true;
                }, ct);

            case CreateContentDraft createDraft:
                return UpdateReturnAsync(createDraft, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    await CreateDraftCore(c, operation);

                    return Snapshot;
                }, ct);

            case DeleteContentDraft deleteDraft:
                return UpdateReturnAsync(deleteDraft, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    DeleteDraftCore(c, operation);

                    return Snapshot;
                }, ct);

            case PatchContent patchContent:
                return UpdateReturnAsync(patchContent, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    await PatchCore(c, operation, ct);

                    return Snapshot;
                }, ct);

            case UpdateContent updateContent:
                return UpdateReturnAsync(updateContent, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    await UpdateCore(c, operation, ct);

                    return Snapshot;
                }, ct);

            case CancelContentSchedule cancelContentSchedule:
                return UpdateReturnAsync(cancelContentSchedule, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    CancelChangeCore(c, operation);

                    return Snapshot;
                }, ct);

            case ChangeContentStatus changeContentStatus:
                return UpdateReturnAsync(changeContentStatus, async (c, ct) =>
                {
                    try
                    {
                        if (c.DueTime > SystemClock.Instance.GetCurrentInstant())
                        {
                            ChangeStatusScheduled(c, c.DueTime.Value);
                        }
                        else
                        {
                            var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                            await ChangeCore(c, operation, ct);
                        }
                    }
                    catch (Exception)
                    {
                        if (Snapshot.ScheduleJob != null && Snapshot.ScheduleJob.Id == c.StatusJobId)
                        {
                            CancelChangeStatus(c);
                        }
                        else
                        {
                            throw;
                        }
                    }

                    return Snapshot;
                }, ct);

            case DeleteContent { Permanent: true } deleteContent:
                return DeletePermanentAsync(deleteContent, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    await DeleteCore(c, operation, ct);
                }, ct);

            case DeleteContent deleteContent:
                return UpdateAsync(deleteContent, async (c, ct) =>
                {
                    var operation = await ContentOperation.CreateAsync(serviceProvider, c, () => Snapshot);

                    await DeleteCore(c, operation, ct);
                }, ct);

            default:
                ThrowHelper.NotSupportedException();
                return default!;
        }
    }

    private async Task CreateCore(CreateContent c, ContentOperation operation,
        CancellationToken ct)
    {
        operation.MustNotCreateComponent();
        operation.MustNotCreateSingleton();
        operation.MustNotCreateForUnpublishedSchema();
        operation.MustHaveData(c.Data);

        if (!c.DoNotValidate)
        {
            await operation.ValidateInputAsync(c.Data, c.OptimizeValidation, Snapshot.IsPublished(), ct);
        }

        var status = await operation.GetInitialStatusAsync();

        if (!c.DoNotScript)
        {
            c.Data = await operation.ExecuteCreateScriptAsync(c.Data, status, ct);
        }

        operation.GenerateDefaultValues(c.Data);

        if (!c.DoNotValidate)
        {
            await operation.ValidateContentAsync(c.Data, c.OptimizeValidation, Snapshot.IsPublished(), ct);
        }

        Create(c, status);
    }

    private async Task ChangeCore(ChangeContentStatus c, ContentOperation operation,
        CancellationToken ct)
    {
        operation.MustHavePermission(PermissionIds.AppContentsChangeStatus);
        operation.MustNotChangeSingleton(c.Status);

        if (c.Status == Snapshot.EditingStatus())
        {
            return;
        }

        if (c.DoNotValidateWorkflow)
        {
            await operation.CheckStatusAsync(c.Status);
        }
        else
        {
            await operation.CheckTransitionAsync(c.Status);
        }

        if (!c.DoNotScript)
        {
            var newData = await operation.ExecuteChangeScriptAsync(c.Status, GetChange(c.Status), ct);

            if (!newData.Equals(Snapshot.Data))
            {
                var previousEvent =
                   GetUncomittedEvents().Select(x => x.Payload)
                       .OfType<ContentDataCommand>().FirstOrDefault();

                if (previousEvent != null)
                {
                    previousEvent.Data = newData;
                }
                else if (!newData.Equals(Snapshot.Data))
                {
                    Update(c, newData);
                }
            }
        }

        if (c.CheckReferrers && Snapshot.IsPublished())
        {
            await operation.CheckReferrersAsync(ct);
        }

        if (!c.DoNotValidate && await operation.ShouldValidateAsync(c.Status))
        {
            await operation.ValidateContentAndInputAsync(Snapshot.Data, c.OptimizeValidation, true, ct);
        }

        ChangeStatus(c);
    }

    private async Task UpdateCore(UpdateContent c, ContentOperation operation,
        CancellationToken ct)
    {
        operation.MustHavePermission(PermissionIds.AppContentsUpdate);
        operation.MustHaveData(c.Data);

        if (!c.DoNotValidate)
        {
            await operation.ValidateInputAsync(c.Data, c.OptimizeValidation, Snapshot.IsPublished(), ct);
        }

        if (!c.DoNotValidateWorkflow)
        {
            await operation.CheckUpdateAsync();
        }

        var newData = c.Data;

        if (newData.Equals(Snapshot.Data))
        {
            return;
        }

        if (!c.DoNotScript)
        {
            newData = await operation.ExecuteUpdateScriptAsync(newData, ct);
        }

        if (!c.DoNotValidate)
        {
            await operation.ValidateContentAsync(newData, c.OptimizeValidation, Snapshot.IsPublished(), ct);
        }

        Update(c, newData);
    }

    private async Task PatchCore(UpdateContent c, ContentOperation operation,
        CancellationToken ct)
    {
        operation.MustHavePermission(PermissionIds.AppContentsUpdate);
        operation.MustHaveData(c.Data);

        if (!c.DoNotValidate)
        {
            await operation.ValidateInputPartialAsync(c.Data, c.OptimizeValidation, Snapshot.IsPublished(), ct);
        }

        if (!c.DoNotValidateWorkflow)
        {
            await operation.CheckUpdateAsync();
        }

        var newData = c.Data.MergeInto(Snapshot.Data);

        if (newData.Equals(Snapshot.Data))
        {
            return;
        }

        if (!c.DoNotScript)
        {
            newData = await operation.ExecuteUpdateScriptAsync(newData, ct);
        }

        if (!c.DoNotValidate)
        {
            await operation.ValidateContentAsync(newData, c.OptimizeValidation, Snapshot.IsPublished(), ct);
        }

        Update(c, newData);
    }

    private void CancelChangeCore(CancelContentSchedule c, ContentOperation operation)
    {
        operation.MustHavePermission(PermissionIds.AppContentsChangeStatusCancel);

        if (Snapshot.ScheduleJob != null)
        {
            CancelChangeStatus(c);
        }
    }

    private async Task ValidateCore(ContentOperation operation,
        CancellationToken ct)
    {
        operation.MustHavePermission(PermissionIds.AppContentsRead);

        await operation.ValidateContentAndInputAsync(Snapshot.Data, false, Snapshot.IsPublished(), ct);
    }

    private async Task CreateDraftCore(CreateContentDraft c, ContentOperation operation)
    {
        operation.MustHavePermission(PermissionIds.AppContentsVersionCreate);
        operation.MustCreateDraft();

        var status = await operation.GetInitialStatusAsync();

        CreateDraft(c, status);
    }

    private void DeleteDraftCore(DeleteContentDraft c, ContentOperation operation)
    {
        operation.MustHavePermission(PermissionIds.AppContentsVersionDelete);
        operation.MustDeleteDraft();

        DeleteDraft(c);
    }

    private async Task DeleteCore(DeleteContent c, ContentOperation operation,
        CancellationToken ct)
    {
        operation.MustHavePermission(PermissionIds.AppContentsDelete);
        operation.MustNotDeleteSingleton();

        if (!c.DoNotScript)
        {
            await operation.ExecuteDeleteScriptAsync(c.Permanent, ct);
        }

        if (c.CheckReferrers)
        {
            await operation.CheckReferrersAsync(ct);
        }

        Delete(c);
    }

    private void Create(CreateContent command, Status status)
    {
        Raise(command, new ContentCreated { Status = status });
    }

    private void Update(ContentCommand command, ContentData data)
    {
        Raise(command, new ContentUpdated { Data = data });
    }

    private void ChangeStatus(ChangeContentStatus command)
    {
        Raise(command, new ContentStatusChanged { Change = GetChange(command.Status) });
    }

    private void ChangeStatusScheduled(ChangeContentStatus command, Instant dueTime)
    {
        Raise(command, new ContentStatusScheduled { DueTime = dueTime });
    }

    private void CancelChangeStatus(ContentCommand command)
    {
        Raise(command, new ContentSchedulingCancelled());
    }

    private void CreateDraft(CreateContentDraft command, Status status)
    {
        Raise(command, new ContentDraftCreated { Status = status });
    }

    private void Delete(DeleteContent command)
    {
        Raise(command, new ContentDeleted());
    }

    private void DeleteDraft(DeleteContentDraft command)
    {
        Raise(command, new ContentDraftDeleted());
    }

    private void Raise<T, TEvent>(T command, TEvent @event) where T : class where TEvent : AppEvent
    {
        RaiseEvent(Envelope.Create(SimpleMapper.Map(command, @event)));
    }

    private StatusChange GetChange(Status status)
    {
        if (status == Status.Published)
        {
            return StatusChange.Published;
        }
        else if (Snapshot.IsPublished())
        {
            return StatusChange.Unpublished;
        }
        else
        {
            return StatusChange.Change;
        }
    }
}
