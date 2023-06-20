﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.LanguageServer.Common;
using Microsoft.AspNetCore.Razor.PooledObjects;
using Microsoft.AspNetCore.Razor.ProjectSystem;
using Microsoft.AspNetCore.Razor.Serialization;
using Microsoft.AspNetCore.Razor.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Razor.Workspaces.ProjectSystem;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Razor.LanguageServer.ProjectSystem;

internal class DefaultRazorProjectService : RazorProjectService
{
    private readonly ProjectSnapshotManagerAccessor _projectSnapshotManagerAccessor;
    private readonly ProjectSnapshotManagerDispatcher _projectSnapshotManagerDispatcher;
    private readonly RemoteTextLoaderFactory _remoteTextLoaderFactory;
    private readonly SnapshotResolver _snapshotResolver;
    private readonly DocumentVersionCache _documentVersionCache;
    private readonly ILogger _logger;

    public DefaultRazorProjectService(
        ProjectSnapshotManagerDispatcher projectSnapshotManagerDispatcher,
        RemoteTextLoaderFactory remoteTextLoaderFactory,
        SnapshotResolver snapshotResolver,
        DocumentVersionCache documentVersionCache,
        ProjectSnapshotManagerAccessor projectSnapshotManagerAccessor,
        ILoggerFactory loggerFactory)
    {
        _projectSnapshotManagerDispatcher = projectSnapshotManagerDispatcher ?? throw new ArgumentNullException(nameof(projectSnapshotManagerDispatcher));
        _remoteTextLoaderFactory = remoteTextLoaderFactory ?? throw new ArgumentNullException(nameof(remoteTextLoaderFactory));
        _snapshotResolver = snapshotResolver ?? throw new ArgumentNullException(nameof(snapshotResolver));
        _documentVersionCache = documentVersionCache ?? throw new ArgumentNullException(nameof(documentVersionCache));
        _projectSnapshotManagerAccessor = projectSnapshotManagerAccessor ?? throw new ArgumentNullException(nameof(projectSnapshotManagerAccessor));
        _logger = loggerFactory?.CreateLogger<DefaultRazorProjectService>() ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public override void AddDocument(string filePath)
    {
        _projectSnapshotManagerDispatcher.AssertDispatcherThread();

        var textDocumentPath = FilePathNormalizer.Normalize(filePath);
        if (_snapshotResolver.TryResolveDocument(textDocumentPath, out var _))
        {
            // Document already added. This usually occurs when VSCode has already pre-initialized
            // open documents and then we try to manually add all known razor documents.
            return;
        }

        if (!_snapshotResolver.TryResolveProject(textDocumentPath, out var projectSnapshot))
        {
            projectSnapshot = _snapshotResolver.GetMiscellaneousProject();
        }

        var targetFilePath = textDocumentPath;
        var projectDirectory = FilePathNormalizer.GetDirectory(projectSnapshot.FilePath);
        if (targetFilePath.StartsWith(projectDirectory, FilePathComparison.Instance))
        {
            // Make relative
            targetFilePath = textDocumentPath[projectDirectory.Length..];
        }

        // Representing all of our host documents with a re-normalized target path to workaround GetRelatedDocument limitations.
        var normalizedTargetFilePath = targetFilePath.Replace('/', '\\').TrimStart('\\');

        var hostDocument = new HostDocument(textDocumentPath, normalizedTargetFilePath);
        var defaultProject = (ProjectSnapshot)projectSnapshot;
        var textLoader = _remoteTextLoaderFactory.Create(textDocumentPath);

        _logger.LogInformation("Adding document '{filePath}' to project '{projectSnapshotFilePath}'.", filePath, projectSnapshot.FilePath);
        _projectSnapshotManagerAccessor.Instance.DocumentAdded(defaultProject.HostProject, hostDocument, textLoader);
    }

    public override void OpenDocument(string filePath, SourceText sourceText, int version)
    {
        _projectSnapshotManagerDispatcher.AssertDispatcherThread();

        var textDocumentPath = FilePathNormalizer.Normalize(filePath);
        if (!_snapshotResolver.TryResolveDocument(textDocumentPath, out _))
        {
            // Document hasn't been added. This usually occurs when VSCode trumps all other initialization
            // processes and pre-initializes already open documents.
            AddDocument(filePath);
        }

        if (!_snapshotResolver.TryResolveProject(textDocumentPath, out var projectSnapshot))
        {
            projectSnapshot = _snapshotResolver.GetMiscellaneousProject();
        }

        var defaultProject = (ProjectSnapshot)projectSnapshot;

        _logger.LogInformation("Opening document '{textDocumentPath}' in project '{projectSnapshotFilePath}'.", textDocumentPath, projectSnapshot.FilePath);
        _projectSnapshotManagerAccessor.Instance.DocumentOpened(defaultProject.HostProject.FilePath, textDocumentPath, sourceText);

        TrackDocumentVersion(textDocumentPath, version);

        if (_snapshotResolver.TryResolveDocument(textDocumentPath, out var documentSnapshot))
        {
            // Start generating the C# for the document so it can immediately be ready for incoming requests.
            _ = documentSnapshot.GetGeneratedOutputAsync();
        }
    }

    public override void CloseDocument(string filePath)
    {
        _projectSnapshotManagerDispatcher.AssertDispatcherThread();

        var textDocumentPath = FilePathNormalizer.Normalize(filePath);
        if (!_snapshotResolver.TryResolveProject(textDocumentPath, out var projectSnapshot))
        {
            projectSnapshot = _snapshotResolver.GetMiscellaneousProject();
        }

        var textLoader = _remoteTextLoaderFactory.Create(filePath);
        var defaultProject = (ProjectSnapshot)projectSnapshot;
        _logger.LogInformation("Closing document '{textDocumentPath}' in project '{projectSnapshotFilePath}'.", textDocumentPath, projectSnapshot.FilePath);
        _projectSnapshotManagerAccessor.Instance.DocumentClosed(defaultProject.HostProject.FilePath, textDocumentPath, textLoader);
    }

    public override void RemoveDocument(string filePath)
    {
        _projectSnapshotManagerDispatcher.AssertDispatcherThread();

        var textDocumentPath = FilePathNormalizer.Normalize(filePath);
        if (!_snapshotResolver.TryResolveProject(textDocumentPath, out var projectSnapshot))
        {
            projectSnapshot = _snapshotResolver.GetMiscellaneousProject();
        }

        if (!projectSnapshot.DocumentFilePaths.Contains(textDocumentPath, FilePathComparer.Instance))
        {
            _logger.LogInformation("Containing project is not tracking document '{filePath}'", filePath);
            return;
        }

        if (projectSnapshot.GetDocument(textDocumentPath) is not DocumentSnapshot documentSnapshot)
        {
            _logger.LogError("Containing project does not contain document '{filePath}'", filePath);
            return;
        }

        var defaultProject = (ProjectSnapshot)projectSnapshot;

        // If the document is open, we can't remove it, because we could still get a request for it, and that
        // request would fail. Instead we move it to the miscellaneous project, just like if we got notified of
        // a remove via the project.razor.json
        if (_projectSnapshotManagerAccessor.Instance.IsDocumentOpen(textDocumentPath))
        {
            _logger.LogInformation("Moving document '{textDocumentPath}' from project '{projectSnapshotFilePath}' to misc files because it is open.", textDocumentPath, projectSnapshot.FilePath);
            var miscellaneousProject = (ProjectSnapshot)_snapshotResolver.GetMiscellaneousProject();
            MoveDocument(textDocumentPath, defaultProject, miscellaneousProject);
        }
        else
        {
            _logger.LogInformation("Removing document '{textDocumentPath}' from project '{projectSnapshotFilePath}'.", textDocumentPath, projectSnapshot.FilePath);
            _projectSnapshotManagerAccessor.Instance.DocumentRemoved(defaultProject.HostProject, documentSnapshot.State.HostDocument);
        }
    }

    public override void UpdateDocument(string filePath, SourceText sourceText, int version)
    {
        _projectSnapshotManagerDispatcher.AssertDispatcherThread();

        var textDocumentPath = FilePathNormalizer.Normalize(filePath);
        if (!_snapshotResolver.TryResolveProject(textDocumentPath, out var projectSnapshot))
        {
            projectSnapshot = _snapshotResolver.GetMiscellaneousProject();
        }

        var defaultProject = (ProjectSnapshot)projectSnapshot;
        _logger.LogTrace("Updating document '{textDocumentPath}'.", textDocumentPath);
        _projectSnapshotManagerAccessor.Instance.DocumentChanged(defaultProject.HostProject.FilePath, textDocumentPath, sourceText);

        TrackDocumentVersion(textDocumentPath, version);
    }

    public override void AddProject(string filePath, string? rootNamespace)
    {
        var normalizedPath = FilePathNormalizer.Normalize(filePath);

        var existingProject = _projectSnapshotManagerAccessor.Instance.GetOrAddLoadedProject(normalizedPath, RazorDefaults.Configuration, rootNamespace ?? RazorDefaults.RootNamespace);
        if (existingProject is null)
        {
            _logger.LogInformation("Added project '{filePath}' to project system.", filePath);
        }

        TryMigrateMiscellaneousDocumentsToProject();
    }

    public override void RemoveProject(string filePath)
    {
        var normalizedPath = FilePathNormalizer.Normalize(filePath);
        if (_projectSnapshotManagerAccessor.Instance.TryRemoveLoadedProject(normalizedPath, out var project))
        {
            _logger.LogInformation("Removing project '{filePath}' from project system.", filePath);
        }

        TryMigrateDocumentsFromRemovedProject(project);
    }

    public override void UpdateProject(
        string filePath,
        RazorConfiguration? configuration,
        string? rootNamespace,
        ProjectWorkspaceState projectWorkspaceState,
        IReadOnlyList<DocumentSnapshotHandle> documents)
    {
        _projectSnapshotManagerDispatcher.AssertDispatcherThread();

        var normalizedPath = FilePathNormalizer.Normalize(filePath);
        var projectDirectory = FilePathNormalizer.GetDirectory(normalizedPath);
        var documentMap = documents.ToImmutableDictionary(document => EnsureFullPath(document.FilePath, projectDirectory), FilePathComparer.Instance);

        if (!projectWorkspaceState.Equals(ProjectWorkspaceState.Default))
        {
            _logger.LogInformation("Updating project '{filePath}' TagHelpers ({projectWorkspaceState.TagHelpers.Count}) and C# Language Version ({projectWorkspaceState.CSharpLanguageVersion}).",
                filePath, projectWorkspaceState.TagHelpers.Length, projectWorkspaceState.CSharpLanguageVersion);
        }

        _projectSnapshotManagerAccessor.Instance.UpdateProject(normalizedPath,
            configuration,
            projectWorkspaceState,
            rootNamespace,
            project => CalculateUpdates(project, documentMap, projectDirectory));
    }

    private ImmutableArray<IUpdateProjectAction> CalculateUpdates(IProjectSnapshot project, ImmutableDictionary<string, DocumentSnapshotHandle> documentMap, string projectDirectory)
    {
        using var _ = ArrayBuilderPool<IUpdateProjectAction>.GetPooledObject(out var builder);
        var miscellaneousProject = _snapshotResolver.GetMiscellaneousProject();

        // "Remove" any unnecessary documents by putting them into the misc project
        foreach (var documentFilePath in project.DocumentFilePaths)
        {
            if (documentMap.ContainsKey(documentFilePath))
            {
                // This document still exists in the updated project
                continue;
            }

            if (project.GetDocument(documentFilePath) is not DocumentSnapshot documentSnapshot)
            {
                // Document isn't in the project? (Or we should throw)?
                continue;
            }

            builder.Add(CreateMoveAction(project, miscellaneousProject, documentSnapshot));
        }

        // Update existing documents
        foreach (var documentFilePath in project.DocumentFilePaths)
        {
            if (!documentMap.TryGetValue(documentFilePath, out var documentHandle))
            {
                // Document exists in the project but not in the configured documents. Chances are the project configuration is from a fallback
                // configuration case (< 2.1) or the project isn't fully loaded yet.
                continue;
            }

            if (project.GetDocument(documentFilePath) is not DocumentSnapshot documentSnapshot)
            {
                continue;
            }

            var currentHostDocument = documentSnapshot.State.HostDocument;
            var newFilePath = EnsureFullPath(documentHandle.FilePath, projectDirectory);
            var newHostDocument = new HostDocument(newFilePath, documentHandle.TargetPath, documentHandle.FileKind);

            if (HostDocumentComparer.Instance.Equals(currentHostDocument, newHostDocument))
            {
                // Current and "new" host documents are equivalent
                continue;
            }

            _logger.LogTrace("Updating document '{newHostDocument.FilePath}''s file kind to '{newHostDocument.FileKind}' and target path to '{newHostDocument.TargetPath}'.",
                newHostDocument.FilePath, newHostDocument.FileKind, newHostDocument.TargetPath);

            var remoteTextLoader = _remoteTextLoaderFactory.Create(newFilePath);
            builder.Add(new UpdateDocumentAction(currentHostDocument, newHostDocument, remoteTextLoader));
        }

        // Add (or migrate from misc) any new documents
        foreach (var documentKvp in documentMap)
        {
            var documentFilePath = documentKvp.Key;
            if (project.DocumentFilePaths.Contains(documentFilePath, FilePathComparer.Instance))
            {
                // Already know about this document
                continue;
            }

            if (miscellaneousProject.DocumentFilePaths.Contains(documentFilePath, FilePathComparer.Instance))
            {
                if (miscellaneousProject.GetDocument(documentFilePath) is not DocumentSnapshot documentSnapshot)
                {
                    continue;
                }

                builder.Add(CreateMoveAction(miscellaneousProject, project, documentSnapshot));
            }
            else
            {
                var documentHandle = documentKvp.Value;
                var remoteTextLoader = _remoteTextLoaderFactory.Create(documentFilePath);
                var newHostDocument = new HostDocument(documentFilePath, documentHandle.TargetPath, documentHandle.FileKind);

                _logger.LogInformation("Adding new document '{documentFilePath}' to project '{projectFilePath}'.", documentFilePath, project.FilePath);
                builder.Add(new AddDocumentAction(newHostDocument, remoteTextLoader));
            }
        }

        return builder.ToImmutable();
    }

    private MoveDocumentAction CreateMoveAction(IProjectSnapshot original, IProjectSnapshot destination, DocumentSnapshot documentSnapshot)
    {
        var hostDocument = documentSnapshot.State.HostDocument;
        var textLoader = new DocumentSnapshotTextLoader(documentSnapshot);

        _logger.LogInformation("Moving '{documentFilePath}' to '{toProject.FilePath}' project.",
            hostDocument.FilePath, destination.FilePath);

        return new MoveDocumentAction(original, destination, documentSnapshot.State.HostDocument, textLoader);
    }

    private void MoveDocument(string documentFilePath, ProjectSnapshot fromProject, ProjectSnapshot toProject)
    {
        Debug.Assert(fromProject.DocumentFilePaths.Contains(documentFilePath, FilePathComparer.Instance));
        Debug.Assert(!toProject.DocumentFilePaths.Contains(documentFilePath, FilePathComparer.Instance));

        if (fromProject.GetDocument(documentFilePath) is not DocumentSnapshot documentSnapshot)
        {
            return;
        }

        var currentHostDocument = documentSnapshot.State.HostDocument;

        var textLoader = new DocumentSnapshotTextLoader(documentSnapshot);
        var newHostDocument = new HostDocument(documentSnapshot.FilePath, documentSnapshot.TargetPath, documentSnapshot.FileKind);

        _logger.LogInformation("Moving '{documentFilePath}' from the '{fromProject.FilePath}' project to '{toProject.FilePath}' project.",
            documentFilePath, fromProject.FilePath, toProject.FilePath);
        _projectSnapshotManagerAccessor.Instance.DocumentRemoved(fromProject.HostProject, currentHostDocument);
        _projectSnapshotManagerAccessor.Instance.DocumentAdded(toProject.HostProject, newHostDocument, textLoader);
    }

    private static string EnsureFullPath(string filePath, string projectDirectory)
    {
        var normalizedFilePath = FilePathNormalizer.Normalize(filePath);
        if (!normalizedFilePath.StartsWith(projectDirectory, FilePathComparison.Instance))
        {
            var absolutePath = Path.Combine(projectDirectory, normalizedFilePath);
            normalizedFilePath = FilePathNormalizer.Normalize(absolutePath);
        }

        return normalizedFilePath;
    }

    // Internal for testing
    internal void TryMigrateDocumentsFromRemovedProject(IProjectSnapshot project)
    {
        _projectSnapshotManagerDispatcher.AssertDispatcherThread();

        var miscellaneousProject = _snapshotResolver.GetMiscellaneousProject();

        foreach (var documentFilePath in project.DocumentFilePaths)
        {
            if (project.GetDocument(documentFilePath) is not DocumentSnapshot documentSnapshot)
            {
                continue;
            }

            if (!_snapshotResolver.TryResolveProject(documentFilePath, out var toProject))
            {
                // This is the common case. It'd be rare for a project to be nested but we need to protect against it anyhow.
                toProject = miscellaneousProject;
            }

            var textLoader = new DocumentSnapshotTextLoader(documentSnapshot);
            var defaultToProject = (ProjectSnapshot)toProject;
            var newHostDocument = new HostDocument(documentSnapshot.FilePath, documentSnapshot.TargetPath, documentSnapshot.FileKind);

            _logger.LogInformation("Migrating '{documentFilePath}' from the '{project.FilePath}' project to '{toProject.FilePath}' project.",
                documentFilePath, project.FilePath, toProject.FilePath);
            _projectSnapshotManagerAccessor.Instance.DocumentAdded(defaultToProject.HostProject, newHostDocument, textLoader);
        }
    }

    // Internal for testing
    internal void TryMigrateMiscellaneousDocumentsToProject()
    {
        _projectSnapshotManagerDispatcher.AssertDispatcherThread();

        var miscellaneousProject = _snapshotResolver.GetMiscellaneousProject();

        foreach (var documentFilePath in miscellaneousProject.DocumentFilePaths)
        {
            if (!_snapshotResolver.TryResolveProject(documentFilePath, out var projectSnapshot))
            {
                continue;
            }

            if (miscellaneousProject.GetDocument(documentFilePath) is not DocumentSnapshot documentSnapshot)
            {
                continue;
            }

            // Remove from miscellaneous project
            var defaultMiscProject = (ProjectSnapshot)miscellaneousProject;
            _projectSnapshotManagerAccessor.Instance.DocumentRemoved(defaultMiscProject.HostProject, documentSnapshot.State.HostDocument);

            // Add to new project

            var textLoader = new DocumentSnapshotTextLoader(documentSnapshot);
            var defaultProject = (ProjectSnapshot)projectSnapshot;
            var newHostDocument = new HostDocument(documentSnapshot.FilePath, documentSnapshot.TargetPath);
            _logger.LogInformation("Migrating '{documentFilePath}' from the '{miscellaneousProject.FilePath}' project to '{projectSnapshot.FilePath}' project.",
                documentFilePath, miscellaneousProject.FilePath, projectSnapshot.FilePath);
            _projectSnapshotManagerAccessor.Instance.DocumentAdded(defaultProject.HostProject, newHostDocument, textLoader);
        }
    }

    private void TrackDocumentVersion(string textDocumentPath, int version)
    {
        if (!_snapshotResolver.TryResolveDocument(textDocumentPath, out var documentSnapshot))
        {
            return;
        }

        _documentVersionCache.TrackDocumentVersion(documentSnapshot, version);
    }

    private class DelegatingTextLoader : TextLoader
    {
        private readonly IDocumentSnapshot _fromDocument;
        public DelegatingTextLoader(IDocumentSnapshot fromDocument)
        {
            _fromDocument = fromDocument ?? throw new ArgumentNullException(nameof(fromDocument));
        }
        public override async Task<TextAndVersion> LoadTextAndVersionAsync(
           LoadTextOptions options,
           CancellationToken cancellationToken)
        {
            var sourceText = await _fromDocument.GetTextAsync().ConfigureAwait(false);
            var version = await _fromDocument.GetTextVersionAsync().ConfigureAwait(false);
            var textAndVersion = TextAndVersion.Create(sourceText, version.GetNewerVersion());
            return textAndVersion;
        }
    }
}
