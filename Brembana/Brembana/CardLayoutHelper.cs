using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Brembana;

internal static class CardLayoutHelper
{
    public const int CardsPerRow = 2;
    public const double CardSpacing = 14;
    public const double HoverBleed = 10;

    public static double GetCardWidth(double containerWidth, double fallbackSingleCardWidth = 520)
    {
        if (containerWidth <= 0)
            containerWidth = fallbackSingleCardWidth * CardsPerRow + CardSpacing;

        var available = containerWidth - HoverBleed * 2;
        return Math.Max(240, (available - CardSpacing) / CardsPerRow);
    }

    public static double GetRowWidth(double containerWidth) =>
        GetCardWidth(containerWidth) * CardsPerRow + CardSpacing;

    public static void PrepareCard(FrameworkElement card, double columnWidth)
    {
        var innerWidth = columnWidth - CardHoverHelper.HostMarginH * 2;
        card.Width = innerWidth;
        card.MaxWidth = innerWidth;
        card.HorizontalAlignment = HorizontalAlignment.Left;
    }

    public static Grid CreateRow(IReadOnlyList<FrameworkElement> cardsInRow, double containerWidth)
    {
        var cardWidth = GetCardWidth(containerWidth);
        var row = new Grid
        {
            ColumnSpacing = CardSpacing,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(HoverBleed, HoverBleed, HoverBleed, CardSpacing)
        };

        for (int c = 0; c < CardsPerRow; c++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cardWidth) });

        for (int c = 0; c < cardsInRow.Count; c++)
        {
            var card = cardsInRow[c];
            PrepareCard(card, cardWidth);
            Grid.SetColumn(card, c);
            row.Children.Add(card);
        }

        return row;
    }
}
