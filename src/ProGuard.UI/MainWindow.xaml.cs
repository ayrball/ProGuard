using System;
using System.Collections.Generic;
using System.Windows;
using ProGuard.UI.ViewModels;
using ProGuard.UI.Views;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace ProGuard.UI;

public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigationService;
    private readonly ISnackbarService _snackbarService;
    private readonly Stack<Type> _navigationHistory = new();
    private bool _isNavigatingBack;
    private bool _initialized;

    public MainWindow(MainViewModel viewModel, INavigationService navigationService, ISnackbarService snackbarService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _navigationService = navigationService;
        _snackbarService = snackbarService;

        _navigationService.SetNavigationControl(RootNavigation);
        _snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        
        _navigationHistory.Push(typeof(DashboardPage));
        RootNavigation.Navigate(typeof(DashboardPage));
    }

    private void RootNavigation_ItemInvoked(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item && item.TargetPageType != null)
        {
            if (!_isNavigatingBack)
            {
                _navigationHistory.Push(item.TargetPageType);
            }
            _navigationService.Navigate(item.TargetPageType);
        }
    }

    private void OnBackButtonClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_navigationHistory.Count > 1)
            {
                _isNavigatingBack = true;
                _navigationHistory.Pop(); // Current page
                var previous = _navigationHistory.Pop(); // Previous target
                _navigationHistory.Push(previous);
                _navigationService.Navigate(previous);
                _isNavigatingBack = false;
            }
            else
            {
                _navigationService.Navigate(typeof(DashboardPage));
            }
        }
        catch { }
    }

    private void OnPaneToggleButtonClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
        }
        catch { }
    }
}
