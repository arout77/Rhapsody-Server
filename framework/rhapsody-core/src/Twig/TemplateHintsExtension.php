<?php

declare(strict_types=1);

namespace Rhapsody\Core\Twig;

use Rhapsody\Core\Twig\Hints\TemplateHintsNodeVisitor;
use Twig\Extension\AbstractExtension;

/**
 * Template hints: marks where every template (and, optionally, block) on a page came from.
 *
 * Each rendered template is wrapped in a pair of HTML comments:
 *
 *   <!--rs:tpl {"e":"open","k":"template","p":"C:/site/views/home.twig","n":"home.twig"}-->
 *   ...the template's output...
 *   <!--rs:tpl {"e":"close","ms":1.42}-->
 *
 * Comments never change layout. A viewer (Rhapsody Server's page overlay) turns them into borders and labels.
 * Nothing is added unless the extension is registered, and it is only registered in development when
 * {@see self::requested()} says so, so production output and the production Twig cache are untouched
 * (the cache key includes the registered extensions, so hinted and plain templates never mix).
 *
 * Turn it on with the environment variable RHAPSODY_TEMPLATE_HINTS:
 *   1 / true / templates   mark templates
 *   blocks                 mark templates and the blocks inside them
 */
class TemplateHintsExtension extends AbstractExtension
{
    public const ENV_FLAG = 'RHAPSODY_TEMPLATE_HINTS';

    /** @var list<float|null> start times of the open markers; null where nothing was written */
    private array $stack = [];

    public function __construct(
        protected readonly bool $blocks = false,
        private readonly bool $requireHtml = true,
    ) {
    }

    /** 'templates', 'blocks', or null when hints were not asked for. */
    public static function requested(): ?string
    {
        $v = $_SERVER[self::ENV_FLAG] ?? $_ENV[self::ENV_FLAG] ?? getenv(self::ENV_FLAG);
        if (! is_string($v)) {
            return null;
        }
        $v = strtolower(trim($v));

        return match ($v) {
            '1', 'true', 'on', 'yes', 'templates' => 'templates',
            'blocks' => 'blocks',
            default  => null,
        };
    }

    /** Registers the extension on $twig when development mode is on and hints were asked for. Returns whether it did. */
    public static function register(\Twig\Environment $twig, bool $isDevelopment): bool
    {
        $mode = $isDevelopment ? self::requested() : null;
        if ($mode === null || $twig->hasExtension(self::class)) {
            return false;
        }
        $twig->addExtension($mode === 'blocks' ? new TemplateHintsBlocksExtension() : new self());

        return true;
    }

    public function getNodeVisitors(): array
    {
        // Compiled templates call back into this class by name, and Twig's cache key is built from the registered
        // extension class names. A separate class for "blocks" keeps the two modes' compiled templates apart.
        return [new TemplateHintsNodeVisitor($this->blocks, static::class)];
    }

    /** Called from compiled templates. */
    public function open(string $kind, string $path, string $name): string
    {
        if (! $this->canWrite()) {
            $this->stack[] = null;

            return '';
        }
        $this->stack[] = hrtime(true) / 1e6;

        return $this->comment(['e' => 'open', 'k' => $kind, 'p' => $path, 'n' => $name]);
    }

    /** Called from compiled templates. */
    public function close(): string
    {
        $started = array_pop($this->stack);
        if ($started === null) {
            return '';
        }

        return $this->comment(['e' => 'close', 'ms' => round(hrtime(true) / 1e6 - $started, 2)]);
    }

    /** Comments in JSON, CSV, XML or plain text would corrupt it, so only HTML responses get them. */
    private function canWrite(): bool
    {
        if (! $this->requireHtml) {
            return true;
        }
        if (PHP_SAPI === 'cli') {
            return false;
        }
        foreach (headers_list() as $h) {
            if (stripos($h, 'content-type:') === 0) {
                return stripos($h, 'text/html') !== false;
            }
        }

        return true;
    }

    /** @param array<string, mixed> $data */
    private function comment(array $data): string
    {
        $json = json_encode($data, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_HEX_TAG | JSON_HEX_AMP | JSON_PARTIAL_OUTPUT_ON_ERROR) ?: '{}';

        // "--" would end the comment early; the JSON stays valid because \u002d is a hyphen.
        return '<!--rs:tpl ' . str_replace('-', '\u002d', $json) . '-->';
    }
}
