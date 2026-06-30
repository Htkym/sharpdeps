import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import prettier from 'eslint-config-prettier';

export default tseslint.config(
  {
    ignores: [
      'out/**',
      'media/viewer.js',
      'media/viewer.js.map',
      'analyzer/**',
      'node_modules/**',
      '**/*.vsix',
      'esbuild.js',
      'scripts/**',
      'vitest.config.ts',
      'eslint.config.mjs'
    ]
  },
  {
    files: ['src/**/*.ts', 'media/**/*.ts'],
    extends: [js.configs.recommended, ...tseslint.configs.recommended, prettier],
    languageOptions: {
      ecmaVersion: 2021,
      sourceType: 'module'
    },
    rules: {
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }
      ]
    }
  }
);
