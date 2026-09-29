import { defineConfig } from '@playwright/test';
export default defineConfig({ testDir: './e2e', timeout: 120000, use: { baseURL: process.env.FORGEDOCK_TEST_URL ?? 'http://127.0.0.1:5173', headless: true, trace: 'off' }, workers: 1, reporter: 'list' });
