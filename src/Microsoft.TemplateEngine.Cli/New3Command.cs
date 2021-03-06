﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.Mount;
using Microsoft.TemplateEngine.Edge;
using Microsoft.TemplateEngine.Edge.Settings;
using Microsoft.TemplateEngine.Edge.Template;
using Microsoft.TemplateEngine.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.TemplateEngine.Cli
{
    public class New3Command
    {
        private static readonly IReadOnlyCollection<MatchLocation> NameFields = new HashSet<MatchLocation> { MatchLocation.Name, MatchLocation.ShortName, MatchLocation.Alias };
        private ExtendedCommandParser _app;
        private HostSpecificTemplateData _hostSpecificTemplateData;
        private IReadOnlyList<IFilteredTemplateInfo> _matchedTemplates;
        private CommandArgument _templateNameArgument;
        private IFilteredTemplateInfo _unambiguousTemplateToUse;
        private readonly TemplateCreator _templateCreator;
        private readonly TemplateCache _templateCache;
        private readonly AliasRegistry _aliasRegistry;
        private readonly Paths _paths;

        private static readonly Regex LocaleFormatRegex = new Regex(@"
                    ^
                        [a-z]{2}
                        (?:-[A-Z]{2})?
                    $"
            , RegexOptions.IgnorePatternWhitespace);
        private bool _forceAmbiguousFlow;
        private readonly Action<IEngineEnvironmentSettings, IInstaller> _onFirstRun;

        public New3Command(string commandName, ITemplateEngineHost host, Action<IEngineEnvironmentSettings, IInstaller> onFirstRun, ExtendedCommandParser app, CommandArgument templateName)
        {
            host = new ExtendedTemplateEngineHost(host, this);
            EnvironmentSettings = new EngineEnvironmentSettings(host, x => new SettingsLoader(x));
            Installer = new Installer(EnvironmentSettings);
            _templateCreator = new TemplateCreator(EnvironmentSettings);
            _templateCache = new TemplateCache(EnvironmentSettings);
            _aliasRegistry = new AliasRegistry(EnvironmentSettings);
            CommandName = commandName;
            _paths = new Paths(EnvironmentSettings);
            _app = app;
            _templateNameArgument = templateName;
            _onFirstRun = onFirstRun;
        }

        public string Alias => _app.InternalParamValue("--alias");

        public bool DebugAttachHasValue => _app.RemainingArguments.Any(x => x.Equals("--debug:attach", StringComparison.Ordinal));

        public bool ExtraArgsHasValue => _app.InternalParamHasValue("--extra-args");

        public string CommandName { get; }

        public IList<string> Install => _app.InternalParamValueList("--install");

        public static IInstaller Installer { get; set; }

        public bool IsForceFlagSpecified => _app.InternalParamHasValue("--force");

        public bool InstallHasValue => _app.InternalParamHasValue("--install");

        public bool IsHelpFlagSpecified => _app.InternalParamHasValue("--help");

        public bool IsListFlagSpecified => _app.InternalParamHasValue("--list");

        public bool IsQuietFlagSpecified => _app.InternalParamHasValue("--quiet");

        public bool IsShowAllFlagSpecified => _app.InternalParamHasValue("--show-all");

        public string Language => _app.InternalParamValue("--language");

        public string Locale => _app.InternalParamValue("--locale");

        public bool LocaleHasValue => _app.InternalParamHasValue("--locale");

        public string Name => _app.InternalParamValue("--name");

        public string OutputPath => _app.InternalParamValue("--output");

        public string TemplateType => _app.InternalParamValue("--type");

        public string TemplateName
        {
            get
            {
                // prefer the -t param over the argument value
                return !string.IsNullOrEmpty(TemplateType) ? TemplateType : _templateNameArgument.Value;
            }
        }

        public bool SkipUpdateCheck => _app.InternalParamHasValue("--skip-update-check");

        public IFilteredTemplateInfo UnambiguousTemplateToUse
        {
            get
            {
                if (_unambiguousTemplateToUse != null)
                {
                    return _unambiguousTemplateToUse;
                }

                if (_forceAmbiguousFlow || _matchedTemplates == null || _matchedTemplates.Count == 0)
                {
                    return null;
                }

                if (_matchedTemplates.Count == 1)
                {
                    return _unambiguousTemplateToUse = _matchedTemplates.First();
                }

                if (EnvironmentSettings.Host.TryGetHostParamDefault("prefs:language", out string defaultLanguage))
                {
                    IFilteredTemplateInfo match = null;

                    foreach(IFilteredTemplateInfo info in _matchedTemplates)
                    {
                        if (info.Info.Tags == null || info.Info.Tags.TryGetValue("language", out string specificLanguage) && string.Equals(specificLanguage, defaultLanguage, StringComparison.OrdinalIgnoreCase))
                        {
                            if(match == null)
                            {
                                match = info;
                            }
                            else
                            {
                                return null;
                            }
                        }
                    }

                    return _unambiguousTemplateToUse = match;
                }

                return null;
            }
        }

        public EngineEnvironmentSettings EnvironmentSettings { get; private set; }

        public static int Run(string commandName, ITemplateEngineHost host, Action<IEngineEnvironmentSettings, IInstaller> onFirstRun, string[] args)
        {
            ExtendedCommandParser app = new ExtendedCommandParser()
            {
                Name = $"dotnet {commandName}",
                FullName = LocalizableStrings.CommandDescription
            };
            SetupInternalCommands(app);

            CommandArgument templateName = app.Argument("template", LocalizableStrings.TemplateArgumentHelp);
            New3Command instance = new New3Command(commandName, host, onFirstRun, app, templateName);

            app.OnExecute(instance.ExecuteAsync);

            int result;
            try
            {
                using (Timing.Over("Execute"))
                {
                    result = app.Execute(args);
                }
            }
            catch (Exception ex)
            {
                AggregateException ax = ex as AggregateException;

                while (ax != null && ax.InnerExceptions.Count == 1)
                {
                    ex = ax.InnerException;
                    ax = ex as AggregateException;
                }

                Reporter.Error.WriteLine(ex.Message.Bold().Red());

                while (ex.InnerException != null)
                {
                    ex = ex.InnerException;
                    ax = ex as AggregateException;

                    while (ax != null && ax.InnerExceptions.Count == 1)
                    {
                        ex = ax.InnerException;
                        ax = ex as AggregateException;
                    }

                    Reporter.Error.WriteLine(ex.Message.Bold().Red());
                }

                Reporter.Error.WriteLine(ex.StackTrace.Bold().Red());
                result = 1;
            }

            return result;
        }

        private void ConfigureEnvironment()
        {
            _onFirstRun?.Invoke(EnvironmentSettings, Installer);

            foreach (Type type in typeof(New3Command).GetTypeInfo().Assembly.GetTypes())
            {
                EnvironmentSettings.SettingsLoader.Components.Register(type);
            }
        }

        private async Task<CreationResultStatus> CreateTemplateAsync(ITemplateInfo template)
        {
            string fallbackName = new DirectoryInfo(OutputPath ?? Directory.GetCurrentDirectory()).Name;
            TemplateCreationResult instantiateResult;

            try
            {
                instantiateResult = await _templateCreator.InstantiateAsync(template, Name, fallbackName, OutputPath, _app.AllTemplateParams, SkipUpdateCheck, IsForceFlagSpecified).ConfigureAwait(false);
            }
            catch (ContentGenerationException cx)
            {
                Reporter.Error.WriteLine(cx.Message.Bold().Red());
                if(cx.InnerException != null)
                {
                    Reporter.Error.WriteLine(cx.InnerException.Message.Bold().Red());
                }

                return CreationResultStatus.CreateFailed;
            }
            catch (TemplateAuthoringException tae)
            {
                Reporter.Error.WriteLine(tae.Message.Bold().Red());
                return CreationResultStatus.CreateFailed;
            }

            string resultTemplateName = string.IsNullOrEmpty(instantiateResult.TemplateFullName) ? TemplateName : instantiateResult.TemplateFullName;

            switch (instantiateResult.Status)
            {
                case CreationResultStatus.Success:
                    Reporter.Output.WriteLine(string.Format(LocalizableStrings.CreateSuccessful, resultTemplateName));
                    HandlePostActions(instantiateResult);
                    break;
                case CreationResultStatus.CreateFailed:
                    Reporter.Error.WriteLine(string.Format(LocalizableStrings.CreateFailed, resultTemplateName, instantiateResult.Message).Bold().Red());
                    break;
                case CreationResultStatus.MissingMandatoryParam:
                    Reporter.Error.WriteLine(string.Format(LocalizableStrings.MissingRequiredParameter, instantiateResult.Message, resultTemplateName).Bold().Red());
                    break;
                case CreationResultStatus.OperationNotSpecified:
                    break;
                case CreationResultStatus.InvalidParamValues:
                case CreationResultStatus.NotFound:
                    ShowTemplateHelp();
                    break;
                default:
                    break;
            }

            return instantiateResult.Status;
        }

        private void HandlePostActions(TemplateCreationResult creationResult)
        {
            if (creationResult.Status != CreationResultStatus.Success)
            {
                return;
            }

            PostActionDispatcher postActionDispatcher = new PostActionDispatcher(creationResult, EnvironmentSettings.SettingsLoader.Components);
            postActionDispatcher.Process();
        }

        private void DisplayTemplateList(bool showAll = false)
        {
            IReadOnlyCollection<ITemplateInfo> results = null;

            try
            {
                if (showAll)
                {
                    results = PerformAllTemplatesInContextQueryAsync().Result.Where(x => x.IsMatch).Select(x => x.Info).ToList();

                    if(results.Count == 0)
                    {
                        results = PerformAllTemplatesInContextQueryAsync().Result.Select(x => x.Info).ToList();
                    }
                }
                else
                {
                    results = _matchedTemplates.Where(x => x.IsMatch).Select(x => x.Info).ToList();

                    if (results.Count == 0)
                    {
                        results = _matchedTemplates.Select(x => x.Info).ToList();
                    }
                }
            }
            catch (TemplateAuthoringException tae)
            {
                Reporter.Error.WriteLine(tae.Message.Bold().Red());
                return;
            }

            IEnumerable<IGrouping<string, ITemplateInfo>> grouped = results.GroupBy(x => x.GroupIdentity);
            EnvironmentSettings.Host.TryGetHostParamDefault("prefs:language", out string defaultLanguage);

            Dictionary<ITemplateInfo, string> templatesVersusLanguages = new Dictionary<ITemplateInfo, string>();

            foreach (IGrouping<string, ITemplateInfo> grouping in grouped)
            {
                List<string> languages = new List<string>();

                foreach (ITemplateInfo info in grouping)
                {
                    if (info.Tags != null && info.Tags.TryGetValue("language", out string lang))
                    {
                        if (string.IsNullOrEmpty(Language) && string.Equals(defaultLanguage, lang, StringComparison.OrdinalIgnoreCase))
                        {
                            lang = $"[{lang}]";
                        }

                        languages.Add(lang);
                    }
                }

                templatesVersusLanguages[grouping.First()] = string.Join(", ", languages);
            }

            HelpFormatter<KeyValuePair<ITemplateInfo, string>> formatter = HelpFormatter.For(EnvironmentSettings, templatesVersusLanguages, 6, '-', false)
                .DefineColumn(t => t.Key.Name, LocalizableStrings.Templates)
                .DefineColumn(t => t.Key.ShortName, LocalizableStrings.ShortName)
                .DefineColumn(t => t.Value, out object languageColumn, LocalizableStrings.Language)
                .DefineColumn(t => t.Key.Classifications != null ? string.Join("/", t.Key.Classifications) : null, out object tagsColumn, LocalizableStrings.Tags)
                .OrderByDescending(languageColumn, new NullOrEmptyIsLastStringComparer())
                .OrderBy(tagsColumn);
            Reporter.Output.WriteLine(formatter.Layout());

            if (!IsListFlagSpecified)
            {
                ShowInvocationExamples();
            }
        }

        private Task<CreationResultStatus> EnterAmbiguousTemplateManipulationFlowAsync()
        {
            if (!string.IsNullOrEmpty(TemplateName))
            {
                bool anyPartialMatchesDisplayed = ShowTemplateNameMismatchHelp();
                DisplayTemplateList(!anyPartialMatchesDisplayed);
                return Task.FromResult(CreationResultStatus.NotFound);
            }

            if (!ValidateRemainingParameters())
            {
                return Task.FromResult(CreationResultStatus.InvalidParamValues);
            }    

            if (!string.IsNullOrWhiteSpace(Alias))
            {
                Reporter.Error.WriteLine(LocalizableStrings.InvalidInputSwitch.Bold().Red());
                Reporter.Error.WriteLine("  " + _app.TemplateParamInputFormat("--alias").Bold().Red());
                return Task.FromResult(CreationResultStatus.NotFound);
            }

            if (IsHelpFlagSpecified)
            {
                ShowUsageHelp();
                DisplayTemplateList();
                return Task.FromResult(CreationResultStatus.Success);
            }
            else
            {
                DisplayTemplateList();

                //If we're showing the list because we were asked to, exit with success, otherwise, exit with failure
                if (IsListFlagSpecified)
                {
                    return Task.FromResult(CreationResultStatus.Success);
                }
                else
                {
                    return Task.FromResult(CreationResultStatus.OperationNotSpecified);
                }
            }
        }

        private Task<CreationResultStatus> EnterInstallFlowAsync()
        {
            Installer.InstallPackages(Install.ToList());
            //TODO: When an installer that directly calls into NuGet is available,
            //  return a more accurate representation of the outcome of the operation
            return Task.FromResult(CreationResultStatus.Success);
        }

        private async Task<CreationResultStatus> EnterMaintenanceFlowAsync()
        {
            if (!ValidateRemainingParameters())
            {
                ShowUsageHelp();
                return CreationResultStatus.InvalidParamValues;
            }

            if (InstallHasValue && 
                ((Install.Count > 0) && (Install[0] != null)))
            {
                CreationResultStatus installResult = await EnterInstallFlowAsync().ConfigureAwait(false);

                if(installResult == CreationResultStatus.Success)
                {                    
                    await PerformCoreTemplateQueryAsync().ConfigureAwait(false);
                    DisplayTemplateList();
                }

                return installResult;
            }

            //No other cases specified, we've fallen through to "Usage help + List"
            ShowUsageHelp();
            await PerformCoreTemplateQueryAsync().ConfigureAwait(false);
            DisplayTemplateList();

            return CreationResultStatus.Success;
        }

        private async Task<CreationResultStatus> EnterSingularTemplateManipulationFlowAsync()
        {
            if (!string.IsNullOrWhiteSpace(Alias))
            {
                if (!ValidateRemainingParameters())
                {
                    ShowUsageHelp();
                    return CreationResultStatus.InvalidParamValues;
                }

                _aliasRegistry.SetTemplateAlias(Alias, UnambiguousTemplateToUse.Info);
                Reporter.Output.WriteLine(LocalizableStrings.AliasCreated);
                return CreationResultStatus.Success;
            }
            else if(IsListFlagSpecified)
            {
                if (!ValidateRemainingParameters())
                {
                    ShowUsageHelp();
                    return CreationResultStatus.InvalidParamValues;
                }

                DisplayTemplateList();
                return CreationResultStatus.Success;
            }
            
            //If we've made it here, we need the actual template's args
            string commandParseFailureMessage = null;
            try
            {
                ParseTemplateArgs(UnambiguousTemplateToUse.Info);
            }
            catch (CommandParserException ex)
            {
                commandParseFailureMessage = ex.Message;
            }

            bool argsError = !ValidateRemainingParameters();

            if (argsError || IsHelpFlagSpecified)
            {
                if(argsError)
                {
                    Reporter.Error.WriteLine(commandParseFailureMessage.Bold().Red());
                }

                ShowUsageHelp();
                ShowTemplateHelp();
                return argsError ? CreationResultStatus.InvalidParamValues : CreationResultStatus.Success;
            }
            else
            {
                try
                {
                    return await CreateTemplateAsync(UnambiguousTemplateToUse.Info).ConfigureAwait(false);
                }
                catch (ContentGenerationException cx)
                {
                    Reporter.Error.WriteLine(cx.Message.Bold().Red());
                    if(cx.InnerException != null)
                    {
                        Reporter.Error.WriteLine(cx.InnerException.Message.Bold().Red());
                    }

                    return CreationResultStatus.CreateFailed;
                }
                catch (Exception ex)
                {
                    Reporter.Error.WriteLine(ex.Message.Bold().Red());
                }

                return CreationResultStatus.CreateFailed;
            }
        }

        private async Task<CreationResultStatus> EnterTemplateManipulationFlowAsync()
        {
            await PerformCoreTemplateQueryAsync().ConfigureAwait(false);

            if (UnambiguousTemplateToUse != null)
            {
                if (UnambiguousTemplateToUse.MatchDisposition.Any(x => x.Kind == MatchKind.Exact && x.Location != MatchLocation.Context && x.Location != MatchLocation.Language))
                {
                    return await EnterSingularTemplateManipulationFlowAsync().ConfigureAwait(false);
                }
                else if(EnvironmentSettings.Host.OnConfirmPartialMatch(UnambiguousTemplateToUse.Info.Name))
                {
                    return await EnterSingularTemplateManipulationFlowAsync().ConfigureAwait(false);
                }
                else
                {
                    return CreationResultStatus.Cancelled;
                }
            }

            return await EnterAmbiguousTemplateManipulationFlowAsync().ConfigureAwait(false);
        }

        private async Task<CreationResultStatus> ExecuteAsync()
        {
            //Parse non-template specific arguments
            try
            {
                _app.ParseArgs();
            }
            catch (CommandParserException ex)
            {
                Reporter.Error.WriteLine(ex.Message.Bold().Red());
                return CreationResultStatus.InvalidParamValues;
            }

            if (ExtraArgsHasValue)
            {
                try
                {
                    _app.ParseArgs(_app.InternalParamValueList("--extra-args"));
                }
                catch (CommandParserException ex)
                {
                    Reporter.Error.WriteLine(ex.Message.Bold().Red());
                    return CreationResultStatus.InvalidParamValues;
                }
            }

            if (DebugAttachHasValue)
            {
                Console.ReadLine();
            }

            ConfigureLocale();
            Initialize();

            try
            {
                if (string.IsNullOrWhiteSpace(TemplateName))
                {
                    return await EnterMaintenanceFlowAsync().ConfigureAwait(false);
                }

                return await EnterTemplateManipulationFlowAsync().ConfigureAwait(false);
            }
            catch (TemplateAuthoringException tae)
            {
                Reporter.Error.WriteLine(tae.Message.Bold().Red());
                return CreationResultStatus.CreateFailed;
            }
        }

        private void ConfigureLocale()
        {
            if (LocaleHasValue)
            {
                string newLocale = Locale;
                if (!ValidateLocaleFormat(newLocale))
                {
                    Reporter.Error.WriteLine(string.Format(LocalizableStrings.BadLocaleError, newLocale).Bold().Red());
                }

                EnvironmentSettings.Host.UpdateLocale(newLocale);
            }
        }

        private IEnumerable<ITemplateParameter> FilterParamsForHelp(IParameterSet allParams, HashSet<string> hiddenParams)
        {
            IEnumerable<ITemplateParameter> filteredParams = allParams.ParameterDefinitions
                .Where(x => x.Priority != TemplateParameterPriority.Implicit && !hiddenParams.Contains(x.Name));
            return filteredParams;
        }

        private void GenerateUsageForTemplate(ITemplateInfo templateInfo)
        {
            ITemplate template = EnvironmentSettings.SettingsLoader.LoadTemplate(templateInfo);
            IParameterSet allParams = template.Generator.GetParametersForTemplate(EnvironmentSettings, template);
            HostSpecificTemplateData hostTemplateData = ReadHostSpecificTemplateData(template);

            Reporter.Output.Write($"    dotnet {CommandName} {template.ShortName}");
            IEnumerable<ITemplateParameter> filteredParams = FilterParamsForHelp(allParams, hostTemplateData.HiddenParameterNames);

            foreach (ITemplateParameter parameter in filteredParams)
            {
                if (string.Equals(parameter.DataType, "bool", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(parameter.DefaultValue, "false", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string displayParameter = hostTemplateData.DisplayNameForParameter(parameter.Name);

                Reporter.Output.Write($" --{displayParameter}");

                if (!string.IsNullOrEmpty(parameter.DefaultValue) && !string.Equals(parameter.DataType, "bool", StringComparison.OrdinalIgnoreCase))
                {
                    Reporter.Output.Write($" {parameter.DefaultValue}");
                }
            }

            Reporter.Output.WriteLine();
        }

        private bool Initialize()
        {
            bool ephemeralHiveFlag = _app.RemainingArguments.Any(x => x == "--debug:ephemeral-hive");

            if (ephemeralHiveFlag)
            {
                EnvironmentSettings.Host.VirtualizeDirectory(_paths.User.BaseDir);
            }

            bool reinitFlag = _app.RemainingArguments.Any(x => x == "--debug:reinit");
            if (reinitFlag)
            {
                _paths.Delete(_paths.User.FirstRunCookie);
            }

            // Note: this leaves things in a weird state. Might be related to the localized caches.
            // not sure, need to look into it.
            if (reinitFlag || _app.RemainingArguments.Any(x => x == "--debug:reset-config"))
            {
                _paths.Delete(_paths.User.AliasesFile);
                _paths.Delete(_paths.User.SettingsFile);
                _templateCache.DeleteAllLocaleCacheFiles();
                return false;
            }

            if (!_paths.Exists(_paths.User.BaseDir) || !_paths.Exists(_paths.User.FirstRunCookie))
            {
                if (!IsQuietFlagSpecified)
                {
                    Reporter.Output.WriteLine(LocalizableStrings.GettingReady);
                }

                ConfigureEnvironment();
                _paths.WriteAllText(_paths.User.FirstRunCookie, "");
            }

            if (_app.RemainingArguments.Any(x => x == "--debug:showconfig"))
            {
                ShowConfig();
                return false;
            }

            return true;
        }

        private void ParameterHelp(IParameterSet allParams, string additionalInfo, HashSet<string> hiddenParams)
        {
            if (!string.IsNullOrEmpty(additionalInfo))
            {
                Reporter.Error.WriteLine(additionalInfo.Bold().Red());
                Reporter.Output.WriteLine();
            }

            IEnumerable<ITemplateParameter> filteredParams = FilterParamsForHelp(allParams, hiddenParams);

            if (filteredParams.Any())
            {
                HelpFormatter<ITemplateParameter> formatter = new HelpFormatter<ITemplateParameter>(EnvironmentSettings, filteredParams, 2, null, true);

                formatter.DefineColumn(
                    param =>
                    {
                        // the key is guaranteed to exist
                        IList<string> variants = _app.CanonicalToVariantsTemplateParamMap[param.Name];
                        string options = string.Join("|", variants.Reverse());
                        return "  " + options;
                    },
                    LocalizableStrings.Options
                );

                formatter.DefineColumn(delegate (ITemplateParameter param)
                {
                    StringBuilder displayValue = new StringBuilder(255);
                    displayValue.AppendLine(param.Documentation);

                    if (string.Equals(param.DataType, "choice", StringComparison.OrdinalIgnoreCase))
                    {
                        int longestChoiceLength = param.Choices.Keys.Max(x => x.Length);

                        foreach (KeyValuePair<string, string> choiceInfo in param.Choices)
                        {
                            displayValue.Append("    " + choiceInfo.Key.PadRight(longestChoiceLength + 4));
                            displayValue.AppendLine("- " + choiceInfo.Value);
                        }
                    }
                    else
                    {
                        displayValue.Append(param.DataType ?? "string");
                        displayValue.AppendLine(" - " + param.Priority.ToString());
                    }

                    // display the configured value if there is one
                    string configuredValue = null;
                    if (allParams.ResolvedValues.TryGetValue(param, out object resolvedValueObject))
                    {
                        string resolvedValue = resolvedValueObject as string;

                        if (!string.IsNullOrEmpty(resolvedValue)
                            && !string.IsNullOrEmpty(param.DefaultValue)
                            && !string.Equals(param.DefaultValue, resolvedValue))
                        {
                            configuredValue = resolvedValue;
                        }
                    }

                    if (string.IsNullOrEmpty(configuredValue))
                    {
                        // this will catch when the user inputs the default value. The above deliberately skips it on the resolved values.
                        if (string.Equals(param.DataType, "bool", StringComparison.OrdinalIgnoreCase)
                            && _app.TemplateParamHasValue(param.Name)
                            && string.IsNullOrEmpty(_app.TemplateParamValue(param.Name)))
                        {
                            configuredValue = "true";
                        }
                        else
                        {
                            _app.AllTemplateParams.TryGetValue(param.Name, out configuredValue);
                        }
                    }

                    if (!string.IsNullOrEmpty(configuredValue))
                    {
                        displayValue.AppendLine(string.Format(LocalizableStrings.ConfiguredValue, configuredValue));
                    }

                    // display the default value if there is one
                    if (!string.IsNullOrEmpty(param.DefaultValue))
                    {
                        displayValue.AppendLine(string.Format(LocalizableStrings.DefaultValue, param.DefaultValue));
                    }

                    return displayValue.ToString();
                }, string.Empty);

                Reporter.Output.WriteLine(formatter.Layout());
            }
            else
            {
                Reporter.Output.WriteLine(LocalizableStrings.NoParameters);
            }
        }

        private void ParseTemplateArgs(ITemplateInfo templateInfo)
        {
            ITemplate template = EnvironmentSettings.SettingsLoader.LoadTemplate(templateInfo);
            IParameterSet allParams = template.Generator.GetParametersForTemplate(EnvironmentSettings, template);
            _hostSpecificTemplateData = ReadHostSpecificTemplateData(template);
            _app.SetupTemplateParameters(allParams, _hostSpecificTemplateData.LongNameOverrides, _hostSpecificTemplateData.ShortNameOverrides);

            // re-parse after setting up the template params
            _app.ParseArgs(_app.InternalParamValueList("--extra-args"));
        }

        private string DetermineTemplateContext()
        {
            string outputPath = OutputPath ?? EnvironmentSettings.Host.FileSystem.GetCurrentDirectory();

            string context = null;
            if (!IsShowAllFlagSpecified)
            {
                if (EnvironmentSettings.Host.FileSystem.DirectoryExists(outputPath) && EnvironmentSettings.Host.FileSystem.EnumerateFiles(outputPath, "*.*proj", SearchOption.TopDirectoryOnly).Any())
                {
                    context = "item";
                }
                else
                {
                    context = "project";
                }
            }

            return context;
        }

        private Task PerformCoreTemplateQueryAsync()
        {
            string context = DetermineTemplateContext();

            //Perform the core query to search for templates
            IReadOnlyCollection<IFilteredTemplateInfo> templates = _templateCreator.List
            (
                false,
                WellKnownSearchFilters.AliasFilter(TemplateName),
                WellKnownSearchFilters.NameFilter(TemplateName),
                WellKnownSearchFilters.ClassificationsFilter(TemplateName),
                WellKnownSearchFilters.LanguageFilter(Language),
                WellKnownSearchFilters.ContextFilter(context)
            );

            IReadOnlyList<IFilteredTemplateInfo> matchedTemplates = templates.Where(x => x.IsMatch).ToList();

            if (matchedTemplates.Count == 0)
            {
                matchedTemplates = templates.Where(x => x.IsPartialMatch).ToList();
                _forceAmbiguousFlow = true;
            }
            else
            {
                IReadOnlyList<IFilteredTemplateInfo> matchesWithExactDispositionsInNameFields = matchedTemplates.Where(x => x.MatchDisposition.Any(y => NameFields.Contains(y.Location) && y.Kind == MatchKind.Exact)).ToList();

                if (matchesWithExactDispositionsInNameFields.Count > 0)
                {
                    matchedTemplates = matchesWithExactDispositionsInNameFields;
                }
            }

            _matchedTemplates = matchedTemplates;

            return Task.FromResult(true);
        }

        // Lists all the templtes, filtered only by the context (item, project, etc)
        private Task<IReadOnlyCollection<IFilteredTemplateInfo>> PerformAllTemplatesInContextQueryAsync()
        {
            string context = DetermineTemplateContext();

            IReadOnlyCollection<IFilteredTemplateInfo> templates = _templateCreator.List(
                false,
                WellKnownSearchFilters.ContextFilter(context),
                WellKnownSearchFilters.NameFilter(string.Empty)
            );

            return Task.FromResult(templates);
        }

        private HostSpecificTemplateData ReadHostSpecificTemplateData(ITemplate templateInfo)
        {
            if (EnvironmentSettings.SettingsLoader.TryGetFileFromIdAndPath(templateInfo.HostConfigMountPointId, templateInfo.HostConfigPlace, out IFile file))
            {
                JObject jsonData;
                using (Stream s = file.OpenRead())
                using (TextReader tr = new StreamReader(s, true))
                using (JsonReader r = new JsonTextReader(tr))
                {
                    jsonData = JObject.Load(r);
                }

                return jsonData.ToObject<HostSpecificTemplateData>();
            }

            return HostSpecificTemplateData.Default;
        }

        private static void SetupInternalCommands(ExtendedCommandParser appExt)
        {
            // visible
            appExt.InternalOption("-l|--list", "--list", LocalizableStrings.ListsTemplates, CommandOptionType.NoValue);
            appExt.InternalOption("-lang|--language", "--language", LocalizableStrings.LanguageParameter, CommandOptionType.SingleValue);
            appExt.InternalOption("-n|--name", "--name", LocalizableStrings.NameOfOutput, CommandOptionType.SingleValue);
            appExt.InternalOption("-o|--output", "--output", LocalizableStrings.OutputPath, CommandOptionType.SingleValue);
            appExt.InternalOption("-h|--help", "--help", LocalizableStrings.DisplaysHelp, CommandOptionType.NoValue);
            appExt.InternalOption("-all|--show-all", "--show-all", LocalizableStrings.ShowsAllTemplates, CommandOptionType.NoValue);
            appExt.InternalOption("--force", "--force", LocalizableStrings.ForcesTemplateCreation, CommandOptionType.NoValue);

            // hidden
            appExt.HiddenInternalOption("-a|--alias", "--alias", CommandOptionType.SingleValue);
            appExt.HiddenInternalOption("-x|--extra-args", "--extra-args", CommandOptionType.MultipleValue);
            appExt.HiddenInternalOption("--locale", "--locale", CommandOptionType.SingleValue);
            appExt.HiddenInternalOption("--quiet", "--quiet", CommandOptionType.NoValue);
            appExt.HiddenInternalOption("-i|--install", "--install", CommandOptionType.MultipleValue);
            appExt.HiddenInternalOption("-t|--type", "--type", CommandOptionType.SingleValue);

            // reserved but not currently used
            appExt.HiddenInternalOption("-up|--update", "--update", CommandOptionType.MultipleValue);
            appExt.HiddenInternalOption("-u|--uninstall", "--uninstall", CommandOptionType.MultipleValue);
            appExt.HiddenInternalOption("--skip-update-check", "--skip-update-check", CommandOptionType.NoValue);
        }

        private void ShowConfig()
        {
            Reporter.Output.WriteLine(LocalizableStrings.CurrentConfiguration);
            Reporter.Output.WriteLine(" ");
            TableFormatter.Print(EnvironmentSettings.SettingsLoader.MountPoints, LocalizableStrings.NoItems, "   ", '-', new Dictionary<string, Func<MountPointInfo, object>>
            {
                {LocalizableStrings.MountPoints, x => x.Place},
                {LocalizableStrings.Id, x => x.MountPointId},
                {LocalizableStrings.Parent, x => x.ParentMountPointId},
                {LocalizableStrings.Factory, x => x.MountPointFactoryId}
            });

            TableFormatter.Print(EnvironmentSettings.SettingsLoader.Components.OfType<IMountPointFactory>(), LocalizableStrings.NoItems, "   ", '-', new Dictionary<string, Func<IMountPointFactory, object>>
            {
                {LocalizableStrings.MountPointFactories, x => x.Id},
                {LocalizableStrings.Type, x => x.GetType().FullName},
                {LocalizableStrings.Assembly, x => x.GetType().GetTypeInfo().Assembly.FullName}
            });

            TableFormatter.Print(EnvironmentSettings.SettingsLoader.Components.OfType<IGenerator>(), LocalizableStrings.NoItems, "   ", '-', new Dictionary<string, Func<IGenerator, object>>
            {
                {LocalizableStrings.Generators, x => x.Id},
                {LocalizableStrings.Type, x => x.GetType().FullName},
                {LocalizableStrings.Assembly, x => x.GetType().GetTypeInfo().Assembly.FullName}
            });
        }

        private void ShowInvocationExamples()
        {
            const int ExamplesToShow = 2;
            IReadOnlyList<string> preferredNameList = new List<string>() { "mvc" };
            int numShown = 0;

            if (_matchedTemplates.Count == 0)
            {
                return;
            }

            List<ITemplateInfo> templateList = _matchedTemplates.Select(x => x.Info).ToList();
            Reporter.Output.WriteLine("Examples:");

            foreach (string preferredName in preferredNameList)
            {
                ITemplateInfo template = templateList.FirstOrDefault(x => string.Equals(x.ShortName, preferredName, StringComparison.OrdinalIgnoreCase));
                if (template != null)
                {
                    GenerateUsageForTemplate(template);
                    numShown++;
                }

                templateList.Remove(template);  // remove it so it won't get chosen again
            }

            // show up to 2 examples (total, including the above)
            Random rnd = new Random();
            for (int i = numShown; i < ExamplesToShow && templateList.Any(); i++)
            {
                int index = rnd.Next(0, templateList.Count - 1);
                ITemplateInfo template = templateList[index];
                GenerateUsageForTemplate(template);
                templateList.Remove(template);  // remove it so it won't get chosen again
            }

            // show a help example
            Reporter.Output.WriteLine($"    dotnet {CommandName} --help");
        }

        private void ShowTemplateHelp()
        {
            ITemplateInfo tmplt = UnambiguousTemplateToUse.Info;

            if (tmplt.Tags != null && tmplt.Tags.TryGetValue("language", out string templateLang) && !string.IsNullOrWhiteSpace(templateLang))
            {
                Reporter.Output.WriteLine($"{tmplt.Name} ({templateLang})");
            }
            else
            {
                Reporter.Output.WriteLine(tmplt.Name);
            }

            if (!string.IsNullOrWhiteSpace(tmplt.Author))
            {
                Reporter.Output.WriteLine(string.Format(LocalizableStrings.Author, tmplt.Author));
            }

            if (!string.IsNullOrWhiteSpace(tmplt.Description))
            {
                Reporter.Output.WriteLine(string.Format(LocalizableStrings.Description, tmplt.Description));
            }

            ITemplate template = EnvironmentSettings.SettingsLoader.LoadTemplate(tmplt);
            IParameterSet allParams = _templateCreator.SetupDefaultParamValuesFromTemplateAndHost(template, template.DefaultName, out IList<string> defaultParamsWithInvalidValues);
            _templateCreator.ResolveUserParameters(template, allParams, _app.AllTemplateParams, out IList<string> userParamsWithInvalidValues);

            string additionalInfo = null;
            if (userParamsWithInvalidValues.Any())
            {
                // Lookup the input param formats - userParamsWithInvalidValues has canonical.
                IList<string> inputParamFormats = new List<string>();
                foreach (string canonical in userParamsWithInvalidValues)
                {
                    string inputFormat = _app.TemplateParamInputFormat(canonical);
                    inputParamFormats.Add(inputFormat);
                }
                string badParams = string.Join(", ", inputParamFormats);

                additionalInfo = string.Format(LocalizableStrings.InvalidParameterValues, badParams, template.Name);
            }

            ParameterHelp(allParams, additionalInfo, _hostSpecificTemplateData.HiddenParameterNames);
        }

        // Returns true if any partial matches were displayed, false otherwise
        private bool ShowTemplateNameMismatchHelp()
        {
            IDictionary<string, IFilteredTemplateInfo> contextProblemMatches = new Dictionary<string, IFilteredTemplateInfo>();
            IDictionary<string, IFilteredTemplateInfo> remainingPartialMatches = new Dictionary<string, IFilteredTemplateInfo>();

            // this filtering / grouping ignores language differences.
            foreach (IFilteredTemplateInfo template in _matchedTemplates)
            {
                if (contextProblemMatches.ContainsKey(template.Info.Name) || remainingPartialMatches.ContainsKey(template.Info.Name))
                {
                    continue;
                }

                if (template.MatchDisposition.Any(x => x.Location == MatchLocation.Context && x.Kind != MatchKind.Exact))
                {
                    contextProblemMatches.Add(template.Info.Name, template);
                }
                else if(template.MatchDisposition.Any(t => t.Location != MatchLocation.Context && t.Kind != MatchKind.Mismatch && t.Kind != MatchKind.Unspecified))
                {
                    remainingPartialMatches.Add(template.Info.Name, template);
                }
            }

            if (contextProblemMatches.Keys.Count + remainingPartialMatches.Keys.Count > 1)
            {
                Reporter.Error.WriteLine(string.Format(LocalizableStrings.AmbiguousInputTemplateName, TemplateName));
            }
            else if (contextProblemMatches.Keys.Count + remainingPartialMatches.Keys.Count == 0)
            {
                Reporter.Error.WriteLine(string.Format(LocalizableStrings.NoTemplatesMatchName, TemplateName));
                Reporter.Error.WriteLine();
                return false;
            }

            foreach (IFilteredTemplateInfo template in contextProblemMatches.Values)
            {
                if (template.Info.Tags != null && template.Info.Tags.TryGetValue("type", out string type))
                {
                    if (string.Equals(type, "item"))
                    {
                        Reporter.Error.WriteLine("  - " + string.Format(LocalizableStrings.ItemTemplateNotInProjectContext, template.Info.Name));
                    }
                    else
                    {   // project template
                        Reporter.Error.WriteLine("  - " + string.Format(LocalizableStrings.ProjectTemplateInProjectContext, template.Info.Name));
                    }
                }
                else
                {   // this really shouldn't ever happen. But better to have a generic error than quietly ignore the partial match.
                    Reporter.Error.WriteLine("  - " + string.Format(LocalizableStrings.GenericPlaceholderTemplateContextError, template.Info.Name));
                }
            }

            if (remainingPartialMatches.Keys.Count > 0)
            {
                Reporter.Error.WriteLine(LocalizableStrings.TemplateMultiplePartialNameMatches);
            }

            Reporter.Error.WriteLine();
            return true;
        }

        private void ShowUsageHelp()
        {
            _app.ShowHelp();
            Reporter.Output.WriteLine();
        }

        private bool ValidateRemainingParameters()
        {
            if (_app.RemainingParameters.Any(x => !x.Key.StartsWith("--debug:")))
            {
                Reporter.Error.WriteLine(LocalizableStrings.InvalidInputSwitch.Bold().Red());
                foreach (string flag in _app.RemainingParameters.Keys)
                {
                    Reporter.Error.WriteLine($"  {flag}".Bold().Red());
                }

                return false;
            }

            return true;
        }

        private static bool ValidateLocaleFormat(string localeToCheck)
        {
            return LocaleFormatRegex.IsMatch(localeToCheck);
        }
    }
}
