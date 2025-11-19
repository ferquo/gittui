#!/usr/bin/env node

const { spawn } = require('child_process');
const path = require('path');
const os = require('os');

const platform = os.platform();
const arch = os.arch();

let binaryName;

if (platform === 'win32') {
    binaryName = 'gittui-win.exe';
} else if (platform === 'linux') {
    binaryName = 'gittui-linux';
} else if (platform === 'darwin') {
    if (arch === 'arm64') {
        binaryName = 'gittui-osx-arm64';
    } else {
        binaryName = 'gittui-osx-x64';
    }
} else {
    console.error(`Unsupported platform: ${platform}`);
    process.exit(1);
}

const binaryPath = path.join(__dirname, 'dist', binaryName);

const child = spawn(binaryPath, process.argv.slice(2), {
    stdio: 'inherit',
    windowsHide: true
});

child.on('close', (code) => {
    process.exit(code);
});

child.on('error', (err) => {
    console.error('Failed to start subprocess:', err);
    process.exit(1);
});
