import { dirname } from 'path';
import { fileURLToPath } from 'url';
import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import globals from 'globals';
import nextPlugin from '@next/eslint-plugin-next';
import reactPlugin from 'eslint-plugin-react';
import reactHooksPlugin from 'eslint-plugin-react-hooks';
import eslintCommentsPlugin from 'eslint-plugin-eslint-comments';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

export default tseslint.config(
  // Global ignores
  {
    ignores: [
      '.next/**',
      'out/**',
      'node_modules/**',
      'src/generated/**',
      '*.js',
      '!*.config.js',
      '!eslint.config.mjs',
    ],
  },

  // Base JS recommended rules
  js.configs.recommended,

  // TypeScript-ESLint recommended with type checking
  ...tseslint.configs.recommendedTypeChecked,

  // Main configuration for TypeScript files
  {
    files: ['**/*.ts', '**/*.tsx'],
    plugins: {
      '@next/next': nextPlugin,
      'react': reactPlugin,
      'react-hooks': reactHooksPlugin,
      'eslint-comments': eslintCommentsPlugin,
    },
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: {
        ...globals.browser,
        ...globals.node,
        React: 'readonly',
        JSX: 'readonly',
      },
      parserOptions: {
        project: './tsconfig.json',
        tsconfigRootDir: __dirname,
        ecmaFeatures: {
          jsx: true,
        },
      },
    },
    settings: {
      react: {
        version: 'detect',
      },
    },
    rules: {
      // Next.js rules
      ...nextPlugin.configs.recommended.rules,
      ...nextPlugin.configs['core-web-vitals'].rules,

      // React rules
      'react/react-in-jsx-scope': 'off',
      'react/prop-types': 'off',
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'warn',

      // TypeScript rules
      '@typescript-eslint/no-unused-vars': 'error',
      '@typescript-eslint/explicit-function-return-type': 'off',
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/no-non-null-assertion': 'warn',
      '@typescript-eslint/strict-boolean-expressions': 'off',
      '@typescript-eslint/no-floating-promises': 'warn',
      '@typescript-eslint/no-misused-promises': 'warn',
      '@typescript-eslint/await-thenable': 'warn',
      '@typescript-eslint/no-unsafe-assignment': 'error',
      '@typescript-eslint/no-unsafe-member-access': 'error',
      '@typescript-eslint/no-unsafe-call': 'error',
      '@typescript-eslint/no-unsafe-return': 'error',
      '@typescript-eslint/require-await': 'off',
      '@typescript-eslint/no-unnecessary-type-assertion': 'warn',
      '@typescript-eslint/prefer-nullish-coalescing': 'warn',
      '@typescript-eslint/prefer-optional-chain': 'warn',
      '@typescript-eslint/no-unsafe-argument': 'error',
      '@typescript-eslint/consistent-type-assertions': [
        'error',
        {
          assertionStyle: 'as',
          objectLiteralTypeAssertions: 'allow',
        },
      ],
      '@typescript-eslint/naming-convention': [
        'error',
        {
          selector: 'variable',
          format: ['camelCase', 'PascalCase', 'UPPER_CASE', 'snake_case'],
          leadingUnderscore: 'forbid',
        },
        {
          selector: 'parameter',
          format: ['camelCase', 'PascalCase', 'snake_case'],
          leadingUnderscore: 'forbid',
        },
        {
          selector: 'property',
          format: ['camelCase', 'PascalCase', 'UPPER_CASE', 'snake_case'],
          leadingUnderscore: 'forbid',
          filter: {
            // Mantine palette keys also define the public CSS variable names.
            regex: '^(Content-Type|Content-Disposition|content-type|content-disposition|X-Master-Key|X-API-Key|custom-rag|openai-compatible|deep-infra|eleven-labs|samba-nova|workers-ai|open-router|meta-ai|conduit-blue|conduit-gray|_retry|max_tokens|top_p|presence_penalty|response_format|aspect_ratio|webhook_url|supportsFunctionCalling|supportsVision|supportsImageGeneration|supportsAudioTranscription|supportsTextToSpeech|supportsRealtimeAudio|supportsStreaming|supportsVideoGeneration|supportsEmbeddings|maxContextLength|maxOutputTokens|isDefault|defaultCapabilityType|_note)$',
            match: false,
          },
        },
      ],

      // eslint-comments rules
      'eslint-comments/no-unlimited-disable': 'error',
      'eslint-comments/no-unused-disable': 'error',
      'eslint-comments/disable-enable-pair': 'error',
      'eslint-comments/no-duplicate-disable': 'error',
      'eslint-comments/no-restricted-disable': [
        'error',
        '@typescript-eslint/no-unsafe-*',
        '@typescript-eslint/naming-convention',
        '@typescript-eslint/no-explicit-any',
      ],

      // General rules
      'no-console': ['warn', { allow: ['warn', 'error'] }],
      eqeqeq: ['error', 'always'],
      'no-debugger': 'error',
      'no-alert': 'error',
      'no-var': 'error',
      'prefer-const': 'error',
      'prefer-arrow-callback': 'error',
      'no-param-reassign': 'error',
      'no-return-await': 'error',
      'no-nested-ternary': 'warn',
      'no-unneeded-ternary': 'error',
      'prefer-template': 'error',
      'no-duplicate-imports': 'warn',
    },
  },

  // Override for JavaScript files
  {
    files: ['**/*.js'],
    rules: {
      '@typescript-eslint/explicit-function-return-type': 'off',
    },
  },

  // Override for test files
  {
    files: ['**/__tests__/**/*', '**/*.test.*', '**/*.spec.*'],
    languageOptions: {
      globals: {
        ...globals.jest,
      },
    },
  },

  // Localized shared primitives preserve the public Common package behavior.
  {
    files: ['src/lib/conduit-common/**/*.ts'],
    rules: {
      '@typescript-eslint/naming-convention': 'off',
      '@typescript-eslint/no-unused-vars': 'off',
      '@typescript-eslint/prefer-nullish-coalescing': 'off',
    },
  },

  // These focused UI helpers were moved without semantic rewrites.
  {
    files: [
      'src/lib/gateway-api/structured-content.ts',
    ],
    rules: {
      '@typescript-eslint/naming-convention': 'off',
      '@typescript-eslint/no-unused-vars': 'off',
    },
  }
);
