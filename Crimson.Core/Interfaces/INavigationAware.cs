using System.Threading.Tasks;

namespace Crimson.Interfaces;
public interface INavigationAware
{
	Task OnNavigatedTo(object parameter);
	void OnNavigatedFrom();
}

