﻿using Nitra.ClientServer.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NitraCommonIde;
using System.Diagnostics;
using Nitra.ClientServer.Messages;

using Ide = NitraCommonIde;
using M = Nitra.ClientServer.Messages;
using Microsoft.VisualStudio.Text.Editor;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using System.Collections.Immutable;
using Nitra.VisualStudio.Highlighting;
using Nitra.VisualStudio.BraceMatching;
using Nitra.VisualStudio.Models;
using System.Diagnostics.Contracts;
using Microsoft.VisualStudio.Shell.Interop;
using WpfHint2;
using System.Windows.Media;

namespace Nitra.VisualStudio
{
  class Server : IDisposable
  {
    Ide.Config _config;
    public IServiceProvider         ServiceProvider { get; }
    public NitraClient              Client          { get; private set; }
    public Hint                     Hint            { get; } = new Hint() { WrapWidth = 900.1 };
    public ImmutableHashSet<string> Extensions      { get; }


    public Server(StringManager stringManager, Ide.Config config, IServiceProvider serviceProvider)
    {
      Contract.Requires(ServiceProvider != null);

      ServiceProvider = serviceProvider;

      var client = new NitraClient(stringManager);
      client.Send(new ClientMessage.CheckVersion(M.Constants.AssemblyVersionGuid));
      var responseMap = client.ResponseMap;
      responseMap[-1] = Response;
      _config = config;
      Client = client;

      var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var lang in config.Languages)
        builder.UnionWith(lang.Extensions);
      Extensions = builder.ToImmutable();
    }

    private ImmutableArray<SpanClassInfo> _spanClassInfos = ImmutableArray<SpanClassInfo>.Empty;
    public ImmutableArray<SpanClassInfo> SpanClassInfos { get { return _spanClassInfos; } }


    private static M.Config ConvertConfig(Ide.Config config)
    {
      var ps = config.ProjectSupport;
      var projectSupport = new M.ProjectSupport(ps.Caption, ps.TypeFullName, ps.Path);
      var languages = config.Languages.Select(x => new M.LanguageInfo(x.Name, x.Path, new M.DynamicExtensionInfo[0])).ToArray();
      var msgConfig = new M.Config(projectSupport, languages, new string[0]);
      return msgConfig;
    }

    public void Dispose()
    {
      Client?.Dispose();
    }

    internal void SolutionStartLoading(SolutionId id, string solutionPath)
    {
      Client.Send(new ClientMessage.SolutionStartLoading(id, solutionPath));
    }

    internal void CaretPositionChanged(FileId fileId, int pos, FileVersion version)
    {
      Client.Send(new ClientMessage.SetCaretPos(fileId, version, pos));
    }

    internal void SolutionLoaded(SolutionId solutionId)
    {
      Client.Send(new ClientMessage.SolutionLoaded(solutionId));
    }

    internal void ProjectStartLoading(ProjectId id, string projectPath)
    {
      var config = ConvertConfig(_config);
      Client.Send(new ClientMessage.ProjectStartLoading(id, projectPath, config));
    }

    internal void ProjectLoaded(ProjectId id)
    {
      Client.Send(new ClientMessage.ProjectLoaded(id));
    }

    internal void ReferenceAdded(ProjectId projectId, string referencePath)
    {
      Client.Send(new ClientMessage.ReferenceLoaded(projectId, "File:" + referencePath));
    }

    internal void AddedMscorlibReference(ProjectId projectId)
    {
      Client.Send(new ClientMessage.ReferenceLoaded(projectId, "FullName:mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"));
    }

    internal void BeforeCloseProject(ProjectId id)
    {
      Client.Send(new ClientMessage.ProjectUnloaded(id));
    }

    internal void FileAdded(ProjectId projectId, string path, FileId id, FileVersion version)
    {
      Client.Send(new ClientMessage.FileLoaded(projectId, path, id, version));
    }

    internal void FileUnloaded(FileId id)
    {
      Client.Send(new ClientMessage.FileUnloaded(id));
    }

    internal void ViewActivated(IWpfTextView wpfTextView, FileId id, IVsHierarchy hierarchy, string fullPath)
    {
      var textBuffer = wpfTextView.TextBuffer;

      TryAddServerProperty(textBuffer);

      FileModel fileModel = VsUtils.GetOrCreateFileModel(wpfTextView, id, this, hierarchy, fullPath);
      TextViewModel textViewModel = VsUtils.GetOrCreateTextViewModel(wpfTextView, fileModel);

      fileModel.ViewActivated(textViewModel);
    }

    void TryAddServerProperty(ITextBuffer textBuffer)
    {
      if (!textBuffer.Properties.ContainsProperty(Constants.ServerKey))
        textBuffer.Properties.AddProperty(Constants.ServerKey, this);
    }

    internal void ViewDeactivated(IWpfTextView wpfTextView, FileId id)
    {
      FileModel fileModel;
      if (wpfTextView.TextBuffer.Properties.TryGetProperty<FileModel>(Constants.FileModelKey, out fileModel))
        fileModel.Remove(wpfTextView);
    }

    internal void DocumentWindowDestroy(IWpfTextView wpfTextView)
    {
      FileModel fileModel;
      if (wpfTextView.TextBuffer.Properties.TryGetProperty<FileModel>(Constants.FileModelKey, out fileModel))
        fileModel.Dispose();
    }

    void Response(AsyncServerMessage msg)
    {
      AsyncServerMessage.LanguageLoaded languageInfo;

      if ((languageInfo = msg as AsyncServerMessage.LanguageLoaded) != null)
      {
        var spanClassInfos = languageInfo.spanClassInfos;
        if (_spanClassInfos.IsDefaultOrEmpty)
          _spanClassInfos = spanClassInfos;
        else if (!spanClassInfos.IsDefaultOrEmpty)
        {
          var bilder = ImmutableArray.CreateBuilder<SpanClassInfo>(_spanClassInfos.Length + spanClassInfos.Length);
          bilder.AddRange(_spanClassInfos);
          bilder.AddRange(spanClassInfos);
          _spanClassInfos = bilder.MoveToImmutable();
        }
      }
    }

    internal SpanClassInfo? GetSpanClassOpt(string spanClass)
    {
      foreach (var spanClassInfo in SpanClassInfos)
        if (spanClassInfo.FullName == spanClass)
          return spanClassInfo;

      return null;
    }

    internal Brush SpanClassToBrush(string spanClass)
    {
      var spanClassOpt = GetSpanClassOpt(spanClass);
      if (spanClassOpt.HasValue)
      {
        // TODO: use classifiers
        var bytes = BitConverter.GetBytes(spanClassOpt.Value.ForegroundColor);
        return new SolidColorBrush(Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]));
      }

      return Brushes.Black;
    }

    public bool IsSupportedExtension(string ext)
    {
      return Extensions.Contains(ext);
    }
  }
}
