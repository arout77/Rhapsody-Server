<?php

declare(strict_types=1);

namespace Rhapsody\Core\Twig\Hints;

use Twig\Attribute\YieldReady;
use Twig\Compiler;
use Twig\Node\Node;

/** Writes the closing marker where a template or block ends. */
#[YieldReady]
final class CloseHintNode extends Node
{
    public function __construct(string $extension)
    {
        parent::__construct([], ['extension' => $extension]);
    }

    public function compile(Compiler $compiler): void
    {
        $compiler
            ->write($compiler->getEnvironment()->useYield() ? 'yield ' : 'echo ')
            ->raw('$this->extensions[')
            ->repr($this->getAttribute('extension'))
            ->raw("]->close();\n");
    }
}
