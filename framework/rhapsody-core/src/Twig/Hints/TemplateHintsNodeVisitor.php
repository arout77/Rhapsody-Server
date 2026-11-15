<?php

declare(strict_types=1);

namespace Rhapsody\Core\Twig\Hints;

use Twig\Environment;
use Twig\Node\BlockNode;
use Twig\Node\BodyNode;
use Twig\Node\ModuleNode;
use Twig\Node\Node;
use Twig\Node\Nodes;
use Twig\NodeVisitor\NodeVisitorInterface;

/** Wraps each template (and optionally each block) in open and close markers while Twig compiles it. */
final class TemplateHintsNodeVisitor implements NodeVisitorInterface
{
    /** Output that is not HTML, or that sits where a comment would show up as text. */
    private const SKIP_TEMPLATE = '~(^|[/\\\\])(emails?|mail)[/\\\\]|\.(txt|json|xml|js|css|svg|ics|csv|md)(\.twig)?$~i';

    /** Blocks that normally land inside a tag, an attribute or a <title>. */
    private const SKIP_BLOCK = '~^(title|page_?title|meta.*|description|keywords|canonical|charset|lang|locale|class|body_?class|html_?class|.*_?attr(ibute)?s?)$~i';

    /** @param class-string $extension the extension class compiled templates call back into */
    public function __construct(private readonly bool $blocks, private readonly string $extension)
    {
    }

    public function enterNode(Node $node, Environment $env): Node
    {
        return $node;
    }

    public function leaveNode(Node $node, Environment $env): ?Node
    {
        if ($node instanceof ModuleNode) {
            $name = (string) $node->getTemplateName();
            if (preg_match(self::SKIP_TEMPLATE, $name) === 1) {
                return $node;
            }
            $path = $this->pathOf($node, $name);
            $node->setNode('display_start', new Nodes([new OpenHintNode($this->extension, 'template', $path, $name), $node->getNode('display_start')]));
            $node->setNode('display_end', new Nodes([$node->getNode('display_end'), new CloseHintNode($this->extension)]));
        } elseif ($this->blocks && $node instanceof BlockNode) {
            $block = (string) $node->getAttribute('name');
            if (preg_match(self::SKIP_BLOCK, $block) === 1) {
                return $node;
            }
            $source = $node->getSourceContext();
            $path   = $source !== null ? $this->clean((string) ($source->getPath() ?: $source->getName())) : '';
            $node->setNode('body', new BodyNode([new OpenHintNode($this->extension, 'block', $path, $block), $node->getNode('body'), new CloseHintNode($this->extension)]));
        }

        return $node;
    }

    public function getPriority(): int
    {
        return 10;
    }

    private function pathOf(ModuleNode $node, string $name): string
    {
        $source = $node->getSourceContext();
        $path   = $source !== null ? (string) $source->getPath() : '';

        return $this->clean($path !== '' ? $path : $name);
    }

    private function clean(string $path): string
    {
        return str_replace('\\', '/', $path);
    }
}
