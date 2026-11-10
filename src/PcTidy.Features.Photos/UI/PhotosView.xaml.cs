using System.Windows.Controls;
using PcTidy.UI.Flow;

namespace PcTidy.Features.Photos.UI;

/// <summary>The Photos page. Fixed height like the standard flow page: only the lists scroll, the action bar stays in view.</summary>
public partial class PhotosView : UserControl, IFixedHeightPage
{
    public PhotosView()
    {
        InitializeComponent();
    }
}
