using System.CommandLine;
using Spectre.Console;

namespace RevitCli.Commands;

public static class CompletionsCommand
{
    public static Command Create()
    {
        var shellArg = new Argument<string>("shell", "Shell type: bash, zsh, powershell");

        var command = new Command("completions", "Generate shell completion script")
        {
            shellArg
        };

        command.SetHandler((shell) =>
        {
            var script = shell.ToLower() switch
            {
                "bash" => GenerateBash(),
                "zsh" => GenerateZsh(),
                "powershell" or "pwsh" => GeneratePowerShell(),
                _ => null
            };

            if (script == null)
            {
                AnsiConsole.MarkupLine($"[red]Unknown shell:[/] {Markup.Escape(shell)}. Use bash, zsh, or powershell.");
                return;
            }

            Console.Write(script);
        }, shellArg);

        return command;
    }

    private static string GenerateBash()
    {
        return """
            _revitcli_completions() {
                local cur="${COMP_WORDS[COMP_CWORD]}"
                local commands="status query export set config audit completions"

                if [ $COMP_CWORD -eq 1 ]; then
                    COMPREPLY=($(compgen -W "$commands" -- "$cur"))
                    return
                fi

                case "${COMP_WORDS[1]}" in
                    query)
                        local opts="--filter --id --output"
                        COMPREPLY=($(compgen -W "$opts" -- "$cur"))
                        ;;
                    export)
                        local opts="--format --sheets --output-dir"
                        COMPREPLY=($(compgen -W "$opts" -- "$cur"))
                        ;;
                    set)
                        local opts="--filter --id --param --value --dry-run"
                        COMPREPLY=($(compgen -W "$opts" -- "$cur"))
                        ;;
                    audit)
                        local opts="--rules --list"
                        COMPREPLY=($(compgen -W "$opts" -- "$cur"))
                        ;;
                    config)
                        local subcmds="show set"
                        COMPREPLY=($(compgen -W "$subcmds" -- "$cur"))
                        ;;
                    completions)
                        local shells="bash zsh powershell"
                        COMPREPLY=($(compgen -W "$shells" -- "$cur"))
                        ;;
                esac
            }
            complete -F _revitcli_completions revitcli
            """;
    }

    private static string GenerateZsh()
    {
        return """
            #compdef revitcli

            _revitcli() {
                local -a commands
                commands=(
                    'status:Check if Revit plugin is online'
                    'query:Query elements from the Revit model'
                    'export:Export sheets or views'
                    'set:Modify element parameters'
                    'config:View or modify CLI configuration'
                    'audit:Run model checking rules'
                    'completions:Generate shell completion script'
                )

                _arguments -C \
                    '1:command:->cmds' \
                    '*::arg:->args'

                case "$state" in
                    cmds)
                        _describe 'command' commands
                        ;;
                    args)
                        case $words[2] in
                            query)
                                _arguments \
                                    '--filter[Filter expression]:filter:' \
                                    '--id[Element ID]:id:' \
                                    '--output[Output format]:format:(table json csv)'
                                ;;
                            export)
                                _arguments \
                                    '--format[Export format]:format:(dwg pdf ifc)' \
                                    '--sheets[Sheet patterns]:sheets:' \
                                    '--output-dir[Output directory]:dir:_directories'
                                ;;
                            set)
                                _arguments \
                                    '--filter[Filter expression]:filter:' \
                                    '--id[Element ID]:id:' \
                                    '--param[Parameter name]:param:' \
                                    '--value[New value]:value:' \
                                    '--dry-run[Preview changes]'
                                ;;
                            audit)
                                _arguments \
                                    '--rules[Comma-separated rules]:rules:' \
                                    '--list[List available rules]'
                                ;;
                            config)
                                _arguments '1:subcommand:(show set)'
                                ;;
                            completions)
                                _arguments '1:shell:(bash zsh powershell)'
                                ;;
                        esac
                        ;;
                esac
            }

            _revitcli
            """;
    }

    private static string GeneratePowerShell()
    {
        return """
            Register-ArgumentCompleter -CommandName revitcli -Native -ScriptBlock {
                param($wordToComplete, $commandAst, $cursorPosition)

                $commands = @{
                    'status' = 'Check if Revit plugin is online'
                    'query' = 'Query elements from the Revit model'
                    'export' = 'Export sheets or views'
                    'set' = 'Modify element parameters'
                    'config' = 'View or modify CLI configuration'
                    'audit' = 'Run model checking rules'
                    'completions' = 'Generate shell completion script'
                }

                $tokens = $commandAst.ToString().Split(' ', [StringSplitOptions]::RemoveEmptyEntries)

                if ($tokens.Count -le 1 -or ($tokens.Count -eq 2 -and $wordToComplete)) {
                    $commands.GetEnumerator() | Where-Object { $_.Key -like "$wordToComplete*" } |
                        ForEach-Object { [System.Management.Automation.CompletionResult]::new($_.Key, $_.Key, 'ParameterValue', $_.Value) }
                }
            }
            """;
    }
}
