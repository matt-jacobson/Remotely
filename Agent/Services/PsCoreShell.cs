﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remotely.Shared.Models;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Threading.Tasks;

namespace Remotely.Agent.Services
{
    public interface IPsCoreShell
    {
        string SenderConnectionId { get; set; }

        CommandCompletion GetCompletions(string inputText, int currentIndex, bool? forward);
        Task<ScriptResult> WriteInput(string input);
    }

    public class PsCoreShell : IPsCoreShell
    {
        private static readonly ConcurrentDictionary<string, IPsCoreShell> _sessions = new();
        private readonly IConfigService _configService;
        private readonly ConnectionInfo _connectionInfo;
        private readonly ILogger<PsCoreShell> _logger;
        private readonly PowerShell _powershell;
        private CommandCompletion _lastCompletion;
        private string _lastInputText;
        public PsCoreShell(
            IConfigService configService,
            ILogger<PsCoreShell> logger)
        {
            _configService = configService;
            _logger = logger;
            _connectionInfo = _configService.GetConnectionInfo();

            _powershell = PowerShell.Create();

            _powershell.AddScript($@"$VerbosePreference = ""Continue"";
                            $DebugPreference = ""Continue"";
                            $InformationPreference = ""Continue"";
                            $WarningPreference = ""Continue"";
                            $env:DeviceId = ""{_connectionInfo.DeviceID}"";
                            $env:ServerUrl = ""{_connectionInfo.Host}""");

            _powershell.Invoke();
        }

        public string SenderConnectionId { get; set; }
        // TODO: Turn into cache and factory.
        public static IPsCoreShell GetCurrent(string senderConnectionId)
        {
            if (_sessions.TryGetValue(senderConnectionId, out var session))
            {
                return session;
            }
            else
            {
                session = Program.Services.GetRequiredService<IPsCoreShell>();
                session.SenderConnectionId = senderConnectionId;
                _sessions.AddOrUpdate(senderConnectionId, session, (id, b) => session);
                return session;
            }
        }

        public CommandCompletion GetCompletions(string inputText, int currentIndex, bool? forward)
        {
            if (_lastCompletion is null ||
                inputText != _lastInputText)
            {
                _lastInputText = inputText;
                _lastCompletion = CommandCompletion.CompleteInput(inputText, currentIndex, new(), _powershell);
            }

            if (forward.HasValue)
            {
                _lastCompletion.GetNextResult(forward.Value);
            }

            return _lastCompletion;
        }

        public async Task<ScriptResult> WriteInput(string input)
        {
            var deviceId = _configService.GetConnectionInfo().DeviceID;
            var sw = Stopwatch.StartNew();

            try
            {

                _powershell.Streams.ClearStreams();
                _powershell.Commands.Clear();

                _powershell.AddScript(input);
                var results = _powershell.Invoke();

                using var ps = PowerShell.Create();
                ps.AddScript("$args[0] | Out-String");
                ps.AddArgument(results);
                var result = await ps.InvokeAsync();
                var hostOutput = result[0].BaseObject.ToString();

                var verboseOut = _powershell.Streams.Verbose.ReadAll().Select(x => x.Message);
                var debugOut = _powershell.Streams.Debug.ReadAll().Select(x => x.Message);
                var errorOut = _powershell.Streams.Error.ReadAll().Select(x => x.Exception.ToString() + Environment.NewLine + x.ScriptStackTrace);
                var infoOut = _powershell.Streams.Information.Select(x => x.MessageData.ToString());
                var warningOut = _powershell.Streams.Warning.Select(x => x.Message);

                var standardOut = hostOutput.Split(Environment.NewLine)
                    .Concat(infoOut)
                    .Concat(debugOut)
                    .Concat(verboseOut);

                var errorAndWarningOut = errorOut.Concat(warningOut).ToArray();


                return new ScriptResult()
                {
                    DeviceID = _configService.GetConnectionInfo().DeviceID,
                    SenderConnectionID = SenderConnectionId,
                    ScriptInput = input,
                    Shell = Shared.Enums.ScriptingShell.PSCore,
                    StandardOutput = standardOut.ToArray(),
                    ErrorOutput = errorAndWarningOut,
                    RunTime = sw.Elapsed,
                    HadErrors = _powershell.HadErrors || errorAndWarningOut.Any()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while writing input to PSCore.");
                return new ScriptResult()
                {
                    DeviceID = deviceId,
                    SenderConnectionID = SenderConnectionId,
                    ScriptInput = input,
                    Shell = Shared.Enums.ScriptingShell.PSCore,
                    StandardOutput = Array.Empty<string>(),
                    ErrorOutput = new[] { "Error while writing input." },
                    RunTime = sw.Elapsed,
                    HadErrors = true
                };
            }
        }
    }
}
