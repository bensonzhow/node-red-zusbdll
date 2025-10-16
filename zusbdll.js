module.exports = function (RED) {
    "use strict";

    const { spawn } = require("child_process");
    const fs = require("fs");
    const fsp = require("fs/promises");
    const os = require("os");
    const path = require("path");

    const EXECUTABLE_NAME = "zdll.exe";
    const EXECUTABLE_DIR = path.join(__dirname, "win-x86");
    const EXECUTABLE_PATH = path.join(EXECUTABLE_DIR, EXECUTABLE_NAME);
    const DEFAULT_SCENARIO_PATH = path.join(EXECUTABLE_DIR, "scenario.json");
    const DEFAULT_ALIAS = "WinSocket";
    const DEFAULT_DLL_RELATIVE = "./ServerModelEncryptionMachine/WinSocketServer.dll";
    const DEFAULT_SEARCH_PATHS = ["./ServerModelEncryptionMachine"];

    function normaliseArgs(args) {
        if (!Array.isArray(args)) {
            return undefined;
        }
        return args.map((arg) => {
            if (arg === null || arg === undefined) {
                return "";
            }
            if (typeof arg === "string") {
                return arg;
            }
            if (typeof arg === "object") {
                if (Object.prototype.hasOwnProperty.call(arg, "raw")) {
                    return String(arg.raw);
                }
                const type = arg.type || arg.t;
                const value = arg.value;
                if (!type) {
                    throw new Error("Argument objects must include a type property.");
                }
                if (value === undefined || value === null) {
                    return `${type}=`;
                }
                return `${type}=${value}`;
            }
            return String(arg);
        });
    }

    function normaliseDllPath(dll) {
        if (!dll) {
            return DEFAULT_DLL_RELATIVE;
        }
        if (path.isAbsolute(dll)) {
            return dll;
        }
        if (/[\\/]/.test(dll)) {
            return dll;
        }
        return `./ServerModelEncryptionMachine/${dll}`;
    }

    function normaliseScenario(scenario) {
        if (!scenario || typeof scenario !== "object") {
            throw new Error("Scenario definition must be an object.");
        }
        const searchPaths = Array.isArray(scenario.searchPaths) && scenario.searchPaths.length > 0
            ? scenario.searchPaths
            : DEFAULT_SEARCH_PATHS;
        const commands = (scenario.commands || []).map((command) => {
            if (!command || typeof command !== "object") {
                throw new Error("Each scenario command must be an object.");
            }
            const copy = { ...command };
            if (Array.isArray(copy.args)) {
                copy.args = normaliseArgs(copy.args);
            }
            return copy;
        });
        if (commands.length === 0) {
            throw new Error("Scenario requires at least one command.");
        }
        return { searchPaths, commands };
    }

    function buildScenarioFromControl(control) {
        if (!control || typeof control !== "object") {
            return null;
        }

        if (control.scenario && typeof control.scenario === "object") {
            return normaliseScenario(control.scenario);
        }

        if (Array.isArray(control)) {
            return normaliseScenario({ commands: control });
        }

        if (Array.isArray(control.commands)) {
            return normaliseScenario({
                searchPaths: control.searchPaths,
                commands: control.commands
            });
        }

        const relevantKeys = [
            "function",
            "calls",
            "dll",
            "dllPath",
            "keepLoaded",
            "openUsbkey",
            "logout",
            "alias",
            "openArgs",
            "logoutArgs"
        ];
        const shouldBuild = relevantKeys.some((key) => Object.prototype.hasOwnProperty.call(control, key));
        if (!shouldBuild) {
            return null;
        }

        const alias = control.alias || DEFAULT_ALIAS;
        const dllPath = normaliseDllPath(control.dllPath || control.dll);
        const searchPaths = Array.isArray(control.searchPaths) && control.searchPaths.length > 0
            ? control.searchPaths
            : DEFAULT_SEARCH_PATHS;
        const commands = [];

        commands.push({ type: "load", path: dllPath, alias });

        const openUsbkey = control.openUsbkey !== false;
        if (openUsbkey) {
            const openFunction = control.openFunction || "OpenUsbkey";
            commands.push({
                type: "call",
                alias,
                function: openFunction,
                args: normaliseArgs(control.openArgs)
            });
        }

        const callList = Array.isArray(control.calls) ? control.calls.slice() : [];
        if (control.function) {
            callList.unshift({ function: control.function, args: control.args, alias: control.callAlias });
        }

        callList.forEach((call) => {
            if (!call || typeof call !== "object") {
                throw new Error("Each call entry must be an object.");
            }
            if (!call.function) {
                throw new Error("Call entry missing function name.");
            }
            const callAlias = call.alias || alias;
            commands.push({
                type: "call",
                alias: callAlias,
                function: call.function,
                args: normaliseArgs(call.args)
            });
        });

        const logout = control.logout !== false;
        if (logout) {
            const logoutFunction = control.logoutFunction || "LgoutServer";
            commands.push({
                type: "call",
                alias,
                function: logoutFunction,
                args: normaliseArgs(control.logoutArgs)
            });
        }

        const keepLoaded = control.keepLoaded === true;
        if (!keepLoaded) {
            commands.push({ type: "unload", alias });
        }

        return normaliseScenario({ searchPaths, commands });
    }

    async function writeScenarioFile(scenario) {
        const dir = await fsp.mkdtemp(path.join(os.tmpdir(), "zusbdll-"));
        const scenarioPath = path.join(dir, "scenario.json");
        await fsp.writeFile(scenarioPath, JSON.stringify(scenario, null, 2), "utf8");
        const cleanup = async () => {
            try {
                if (fsp.rm) {
                    await fsp.rm(dir, { recursive: true, force: true });
                } else {
                    await fsp.rmdir(dir, { recursive: true });
                }
            } catch (err) {
                // ignore cleanup errors
            }
        };
        return { path: scenarioPath, cleanup };
    }

    async function resolveScenarioPath(candidate) {
        const scenarioPath = path.isAbsolute(candidate)
            ? candidate
            : path.join(EXECUTABLE_DIR, candidate);
        await fsp.access(scenarioPath, fs.constants.F_OK);
        return scenarioPath;
    }

    async function prepareScenario(control) {
        if (control === null || control === undefined) {
            return { path: DEFAULT_SCENARIO_PATH, cleanup: null };
        }

        if (typeof control === "string") {
            const scenarioPath = await resolveScenarioPath(control);
            return { path: scenarioPath, cleanup: null };
        }

        if (typeof control === "object") {
            if (control.scenarioFile || control.file || control.path) {
                const scenarioPath = await resolveScenarioPath(control.scenarioFile || control.file || control.path);
                return { path: scenarioPath, cleanup: null };
            }

            const scenario = buildScenarioFromControl(control);
            if (scenario) {
                return writeScenarioFile(scenario);
            }
        }

        return { path: DEFAULT_SCENARIO_PATH, cleanup: null };
    }

    function parseOutput(stdout) {
        const trimmed = stdout ? stdout.trim() : "";
        if (!trimmed) {
            return { primary: null, json: [], text: "" };
        }

        const lines = trimmed.split(/\r?\n/);
        const jsonEntries = [];
        const textEntries = [];

        lines.forEach((line) => {
            const candidate = line.trim();
            if (!candidate) {
                return;
            }
            try {
                jsonEntries.push(JSON.parse(candidate));
            } catch (err) {
                textEntries.push(line);
            }
        });

        let primary = null;
        if (jsonEntries.length === 1 && textEntries.length === 0) {
            primary = jsonEntries[0];
        } else if (jsonEntries.length > 0) {
            primary = jsonEntries;
        } else {
            primary = textEntries.join(os.EOL);
        }

        return { primary, json: jsonEntries, text: textEntries.join(os.EOL) };
    }

    function runExecutable(args) {
        return new Promise((resolve, reject) => {
            const child = spawn(EXECUTABLE_PATH, args, {
                cwd: EXECUTABLE_DIR,
                windowsHide: true
            });
            let stdout = "";
            let stderr = "";

            child.stdout.on("data", (chunk) => {
                stdout += chunk.toString();
            });

            child.stderr.on("data", (chunk) => {
                stderr += chunk.toString();
            });

            child.on("error", (err) => {
                reject(err);
            });

            child.on("close", (code) => {
                resolve({ code, stdout, stderr });
            });
        });
    }

    function zdll(config) {
        RED.nodes.createNode(this, config);
        const node = this;

        node.on("input", async function onInput(msg, send, done) {
            send = send || function defaultSend() { node.send.apply(node, arguments); };

            if (process.platform !== "win32") {
                const err = new Error("zdll node requires Windows to execute zdll.exe (win-x86 target).");
                node.status({ fill: "red", shape: "ring", text: "unsupported platform" });
                node.error(err, msg);
                if (done) {
                    done(err);
                }
                return;
            }

            try {
                await fsp.access(EXECUTABLE_PATH, fs.constants.F_OK);
            } catch (accessErr) {
                const err = new Error(`zdll executable not found at ${EXECUTABLE_PATH}`);
                node.status({ fill: "red", shape: "ring", text: "missing zdll.exe" });
                node.error(err, msg);
                if (done) {
                    done(err);
                }
                return;
            }

            const control = msg.zdllConfig || msg.zdll || msg.payload;
            let scenarioInfo;

            try {
                scenarioInfo = await prepareScenario(control);
            } catch (scenarioErr) {
                node.status({ fill: "red", shape: "ring", text: "invalid scenario" });
                node.error(scenarioErr, msg);
                if (done) {
                    done(scenarioErr);
                }
                return;
            }

            let cleanup = null;
            if (scenarioInfo.cleanup) {
                cleanup = scenarioInfo.cleanup;
            }

            node.status({ fill: "blue", shape: "dot", text: "executing" });

            try {
                const args = ["run", scenarioInfo.path];
                const result = await runExecutable(args);
                const parsed = parseOutput(result.stdout);

                msg.zdll = {
                    exitCode: result.code,
                    stdout: result.stdout,
                    stderr: result.stderr,
                    scenario: scenarioInfo.path,
                    parsed: parsed.json
                };

                if (parsed.primary !== null) {
                    msg.payload = parsed.primary;
                } else {
                    msg.payload = result.stdout;
                }

                if (result.stderr) {
                    node.warn(result.stderr.trim());
                }

                if (result.code !== 0) {
                    const err = new Error(`zdll.exe exited with code ${result.code}`);
                    node.status({ fill: "red", shape: "ring", text: `exit ${result.code}` });
                    node.error(err, msg);
                    if (done) {
                        done(err);
                    }
                    return;
                }

                node.status({ fill: "green", shape: "dot", text: "done" });
                send(msg);
                if (done) {
                    done();
                }
            } catch (err) {
                node.status({ fill: "red", shape: "ring", text: err.message });
                node.error(err, msg);
                if (done) {
                    done(err);
                }
            } finally {
                if (cleanup) {
                    try {
                        await cleanup();
                    } catch (cleanupErr) {
                        node.warn(`Failed to clean temporary scenario: ${cleanupErr.message}`);
                    }
                }
            }
        });

        node.on("close", () => {
            node.status({});
        });
    }

    RED.nodes.registerType("zusbdll", zdll);
};
