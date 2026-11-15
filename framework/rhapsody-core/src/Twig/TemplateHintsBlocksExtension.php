<?php

declare(strict_types=1);

namespace Rhapsody\Core\Twig;

/**
 * Template hints that also mark the blocks inside each template.
 * A class of its own (rather than a flag) so Twig caches "templates" and "blocks" output separately.
 */
final class TemplateHintsBlocksExtension extends TemplateHintsExtension
{
    public function __construct(bool $requireHtml = true)
    {
        parent::__construct(true, $requireHtml);
    }
}
