<?php

declare(strict_types=1);

namespace Rhapsody\Core\Twig\Hints;

use Twig\Attribute\YieldReady;
use Twig\Compiler;
use Twig\Node\Node;

/** Writes the opening marker where a template or block starts. */
#[YieldReady]
final class OpenHintNode extends Node
{
    public function __construct(string $extension, string $kind, string $path, string $name)
    {
        parent::__construct([], ['extension' => $extension, 'kind' => $kind, 'path' => $path, 'name' => $name]);
    }

    public function compile(Compiler $compiler): void
    {
        $compiler
            ->write($compiler->getEnvironment()->useYield() ? 'yield ' : 'echo ')
            ->raw('$this->extensions[')
            ->repr($this->getAttribute('extension'))
            ->raw(']->open(')
            ->repr($this->getAttribute('kind'))
            ->raw(', ')
            ->repr($this->getAttribute('path'))
            ->raw(', ')
            ->repr($this->getAttribute('name'))
            ->raw(");\n");
    }
}
