// zusbdll.js - 生产环境版本（支持会话复用、requestId、崩溃恢复、超时、优雅关闭）

const { spawn } = require("child_process");
const fs = require("fs");
const fsp = require("fs/promises");
const os = require("os");
const path = require("path");

// ===== 配置 =====
const EXECUTABLE_NAME = "zdll.exe";
const EXECUTABLE_DIR = path.join(__dirname, "win-x86");
const EXECUTABLE_PATH = path.join(EXECUTABLE_DIR, EXECUTABLE_NAME);
const DEFAULT_ALIAS = "WinSocket";
const DEFAULT_DLL_RELATIVE = "./ServerModelEncryptionMachine/WinSocketServer.dll";
const DEFAULT_SEARCH_PATHS = ["./ServerModelEncryptionMachine"];
const SESSION_TIMEOUT = 600_000; // 5分钟空闲超时
const CALL_TIMEOUT = 60_000; // 单次调用超时 30秒

// ===== 全局会话管理 =====
const SESSIONS = new Map(); // key: alias, value: session object

module.exports = function (RED) {
    "use strict";

    // ===== 工具函数 =====
    function normaliseArgs(args) {
        if (!Array.isArray(args)) return undefined;
        return args.map(arg => {
            if (arg === null || arg === undefined) return "";
            if (typeof arg === "string") return arg;
            if (typeof arg === "object") {
                if (Object.prototype.hasOwnProperty.call(arg, "raw")) return String(arg.raw);
                const type = arg.type || arg.t;
                const value = arg.value;
                if (!type) throw new Error("Argument objects must include a type property.");
                if (value === undefined || value === null) return `${type}=`;
                return `${type}=${value}`;
            }
            return String(arg);
        });
    }

    function normaliseDllPath(dll) {
        if (!dll) return DEFAULT_DLL_RELATIVE;
        if (path.isAbsolute(dll)) return dll;
        if (/[\\/]/.test(dll)) return dll;
        return `./ServerModelEncryptionMachine/${dll}`;
    }

    // ===== 启动 serve 进程 + 执行初始化 =====
    async function startServeProcess(control, node) {
        const alias = control.alias || DEFAULT_ALIAS;
        const dllPath = normaliseDllPath(control.dllPath || control.dll);
        const searchPaths = Array.isArray(control.searchPaths) ? control.searchPaths : DEFAULT_SEARCH_PATHS;

        const args = ["serve", "--dll", dllPath, "--alias", alias];
        searchPaths.forEach(sp => {
            args.push("--search-path", sp);
        });

        const child = spawn(EXECUTABLE_PATH, args, {
            cwd: EXECUTABLE_DIR,
            windowsHide: true,
            stdio: ["pipe", "pipe", "pipe"]
        });

        const pendingRequests = new Map();
        let stdoutBuffer = "";

        // 处理 stdout
        child.stdout.on("data", chunk => {

            stdoutBuffer += chunk.toString();

            const endsWithNewline = stdoutBuffer.endsWith('\n') || stdoutBuffer.endsWith('\r');


            const lines = stdoutBuffer.split(/\r?\n/);

            const completeLines = endsWithNewline ? lines : lines.slice(0, -1);


            stdoutBuffer = endsWithNewline ? "" : lines[lines.length - 1] || "";

            for (const line of lines) {
                if (!line.trim()) continue;
                try {
                    const obj = JSON.parse(line);
                    if (obj.command === 'serve' && obj.succeeded === 'true') {
                        child.stdout.removeListener('data', online);
                        clearTimeout(timeout);
                        resove();
                        return;
                    }

                    if (obj.requestId) {
                        const req = pendingRequests.get(obj.requestId);
                        if (req) {
                            clearTimeout(req.timeoutId);
                            pendingRequests.delete(obj.requestId);
                            req.resolve(obj.result);
                        }
                    }
                } catch (e) {
                    // ignore parse errors
                }
            }
        });

        // 转发 stderr
        child.stderr.on("data", chunk => {
            const msg = chunk.toString().trim();
            if (msg) RED.log.warn(`[zusbdll] stderr: ${msg}`);
        });

        // 监听退出
        child.on("exit", (code, signal) => {
            //RED.log.warn(`[zusbdll] serve process exited (alias: ${alias}, code: ${code}, signal: ${signal})`);
            cleanupSession(alias);
        });

        // ===== 等待 serve 启动成功 =====
        await new Promise((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error("Serve startup timeout")), 5000);
            const onLine = (chunk) => {
                stdoutBuffer += chunk.toString();
                const endsWithNewline = stdoutBuffer.endsWith('\n') || stdoutBuffer.endsWith('\r');
                const lines = stdoutBuffer.split(/\r?\n/);
                const completeLines = endsWithNewline ? lines : lines.slice(0, -1);
                stdoutBuffer = endsWithNewline ? "" : lines[lines.length - 1] || "";
                for (const line of lines) {
                    if (!line.trim()) continue;
                    try {
                        const obj = JSON.parse(line);
                        if (obj.command === "serve" && obj.succeeded) {
                            child.stdout.removeListener("data", onLine);
                            clearTimeout(timeout);
                            resolve();
                        }
                    } catch (e) { }
                }
            };
            child.stdout.on("data", onLine);
            child.once("error", (err) => {
                clearTimeout(timeout);
                reject(err);
            });
        });
        // ===== 执行初始化调用 =====
        // 默认初始化序列
        const defaultInitCalls = [
            { function: "OpenUsbkey" },
            //{ function: "LgServer", args: control.args || [] }
        ];

        // 用户可覆盖
        const initCalls = control.initCalls || defaultInitCalls;

        for (const call of initCalls) {
            if (!call.function) continue;
            const cmd = {
                action: "call",
                function: call.function,
                returnType: call.returnType || "int",
                args: normaliseArgs(call.args)
            };
            await sendServeCommandDirect(child, pendingRequests, cmd);
        }

        return { child, pendingRequests, alias };
    }

    // 辅助函数：直接发送命令（用于初始化阶段）
    function sendServeCommandDirect(child, pendingRequests, command) {
        return new Promise((resolve, reject) => {
            const requestId = Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
            const timeoutId = setTimeout(() => {
                pendingRequests.delete(requestId);
                reject(new Error(`Initialization timeout after ${CALL_TIMEOUT}ms`));
            }, CALL_TIMEOUT);

            pendingRequests.set(requestId, { resolve, reject, timeoutId });
            const cmdWithId = { ...command, requestId };
            child.stdin.write(JSON.stringify(cmdWithId) + "\n", err => {
                if (err) {
                    pendingRequests.delete(requestId);
                    clearTimeout(timeoutId);
                    reject(err);
                }
            });
        });
    }

    // ===== 发送命令（带 requestId 和超时）=====
    async function sendServeCommand(session, command) {
        const requestId = Date.now().toString(36) + Math.random().toString(36).slice(2, 10);
        return new Promise((resolve, reject) => {
            if (!session.child.stdin.writable) {
                return reject(new Error("Serve process stdin is not writable (process may have exited)"));
            }

            const timeoutId = setTimeout(() => {
                session.pendingRequests.delete(requestId);
                reject(new Error(`Call timeout after ${CALL_TIMEOUT}ms`));
            }, CALL_TIMEOUT);

            session.pendingRequests.set(requestId, { resolve, reject, timeoutId });
            const cmdWithId = { ...command, requestId };
            session.child.stdin.write(JSON.stringify(cmdWithId) + "\n", err => {
                if (err) {
                    session.pendingRequests.delete(requestId);
                    clearTimeout(timeoutId);
                    reject(err);
                }
            });
        });
    }

    // ===== 清理会话 =====
    function cleanupSession(alias) {
        const session = SESSIONS.get(alias);
        if (session) {
            SESSIONS.delete(alias);
            // 尝试优雅退出
            try {
                if (session.child.stdin.writable) {
                    session.child.stdin.end(JSON.stringify({ action: "exit" }) + "\n");
                }
            } catch (e) { }
            // 强制 kill（如果还在运行）
            try {
                session.child.kill();
            } catch (e) { }
            // 清理 pending requests
            for (const [id, req] of session.pendingRequests) {
                clearTimeout(req.timeoutId);
                req.reject(new Error("Session terminated"));
            }
        }
    }

    // ===== 节点定义 =====
    function zdll(config) {
        RED.nodes.createNode(this, config);
        const node = this;

        node.on("input", async function onInput(msg, send, done) {
            send = send || ((...args) => node.send(...args));

            // 平台检查
            if (process.platform !== "win32") {
                const err = new Error("zdll node requires Windows to execute zdll.exe");
                node.status({ fill: "red", shape: "ring", text: "unsupported platform" });
                node.error(err, msg);
                if (done) done(err);
                return;
            }

            // 检查可执行文件
            try {
                await fsp.access(EXECUTABLE_PATH, fs.constants.F_OK);
            } catch (accessErr) {
                const err = new Error(`zdll executable not found at ${EXECUTABLE_PATH}`);
                node.status({ fill: "red", shape: "ring", text: "missing zdll.exe" });
                node.error(err, msg);
                if (done) done(err);
                return;
            }

            const control = msg.zdllConfig || msg.zdll || msg.payload;
            if (!control || typeof control !== "object") {
                const err = new Error("Invalid control object");
                node.status({ fill: "red", shape: "ring", text: "invalid input" });
                node.error(err, msg);
                if (done) done(err);
                return;
            }

            const alias = control.alias || DEFAULT_ALIAS;
            let session = SESSIONS.get(alias);


            // 处理特殊命令（exit/unload）
            if (control.action === "exit" || control.function === "exit") {
                if (session) {
                    try {
                        // 发送 exit 命令
                        await new Promise((resolve, reject) => {
                            const cmd = { action: "exit" };
                            session.child.stdin.write(JSON.stringify(cmd) + "\n", err => {
                                if (err) reject(err);
                                else resolve();
                            });
                        });
                        node.status({ fill: "green", shape: "dot", text: "exited" });
                        send(msg);
                    } catch (err) {
                        node.error(err, msg);
                    }
                } else {
                    // node.warn(`No active session for alias: ${alias}`);
                    send(msg);
                }
                if (done) done();
                return;
            }

            if (control.action === "unload" || control.function === "unload") {
                if (session) {
                    try {
                        const result = await sendServeCommand(session, { action: "unload" });
                        msg.payload = result;
                        node.status({ fill: "green", shape: "dot", text: "unloaded" });
                        send(msg);
                    } catch (err) {
                        node.error(err, msg);
                    }
                } else {
                    // node.warn(`No active session for alias: ${alias}`);
                    send(msg);
                }
                if (done) done();
                return;
            }

            // 创建新会话（仅首次）
            if (!session) {
                try {
                    const newSession = await startServeProcess(control, node);
                    //node.warn(newSession)
                    SESSIONS.set(alias, newSession);

                    session = newSession;

                    const timeoutId = setTimeout(() => {
                        if (SESSIONS.get(alias) === session) {
                            cleanupSession(alias);
                        }
                    }, SESSION_TIMEOUT);
                    session.timeoutId = timeoutId;
                } catch (err) {
                    node.status({ fill: "red", shape: "ring", text: "spawn failed" });
                    node.error(err, msg);
                    if (done) done(err);
                    return;
                }
            } else {
                // 更新空闲超时
                clearTimeout(session.timeoutId);
                session.timeoutId = setTimeout(() => {
                    if (SESSIONS.get(alias) === session) {
                        cleanupSession(alias);
                    }
                }, SESSION_TIMEOUT);
            }

            // 构造调用命令
            // ===== 支持 calls 数组 =====
            let results = [];
            let allSucceeded = true;

            if (Array.isArray(control.calls) && control.calls.length > 0) {
                // 批量调用模式
                node.status({ fill: "blue", shape: "dot", text: `calling ${control.calls.length}...` });

                for (const call of control.calls) {
                    if (!call || typeof call !== "object" || !call.function) {
                        const err = new Error("Each call must be an object with 'function'");
                        node.error(err, msg);
                        if (done) done(err);
                        return;
                    }

                    const serveCmd = {
                        action: "call",
                        function: call.function,
                        returnType: call.returnType || "int",
                        args: normaliseArgs(call.args)
                    };

                    try {
                        const resultJson = await sendServeCommand(session, serveCmd);
                        results.push(resultJson);
                        if (!resultJson?.succeeded) allSucceeded = false;
                    } catch (err) {
                        node.status({ fill: "red", shape: "ring", text: "batch call error" });
                        node.error(err, msg);
                        if (done) done(err);
                        return;
                    }
                }
            } else if (control.function) {
                // 单次调用模式（原有逻辑）
                node.status({ fill: "blue", shape: "dot", text: "calling..." });

                const serveCmd = {
                    action: "call",
                    function: control.function,
                    returnType: control.returnType || "int",
                    args: normaliseArgs(control.args)
                };

                try {
                    const resultJson = await sendServeCommand(session, serveCmd);
                    results = [resultJson];
                    allSucceeded = resultJson?.succeeded === true;
                } catch (err) {
                    node.status({ fill: "red", shape: "ring", text: "timeout/error" });
                    node.error(err, msg);
                    if (done) done(err);
                    return;
                }
            } else {
                const err = new Error("Missing 'function' or 'calls' in control");
                node.status({ fill: "red", shape: "ring", text: "no function/calls" });
                node.error(err, msg);
                if (done) done(err);
                return;
            }

            // ===== 构造输出 =====
            const exitCode = allSucceeded ? 0 : 1;
            msg.zdll = {
                exitCode,
                stdout: JSON.stringify(results, null, 2),
                stderr: "",
                scenario: "interactive",
                parsed: results
            };
            msg.payload = results.length === 1 ? results[0] : results;

            if (!allSucceeded) {
                const firstError = results.find(r => !r.succeeded)?.error || 'unknown error';
                const err = new Error(`Batch call failed: ${firstError}`);
                node.status({ fill: "red", shape: "ring", text: "batch failed" });
                node.error(err, msg);
                if (done) done(err);
                return;
            }

            node.status({ fill: "green", shape: "dot", text: "done" });
            send(msg);
            if (done) done();
        });

        // 优雅关闭
        node.on("close", () => {
            node.status({});
            for (const alias of SESSIONS.keys()) {
                cleanupSession(alias);
            }
        });
    }

    RED.nodes.registerType("zusbdll", zdll);
};